using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Notifications;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Modules.Identity.Commands.RequestPasswordReset;

/// <summary>
/// Issues a single-use, time-limited reset token and hands it to the notifier (FR-AUTH-012).
/// </summary>
/// <remarks>
/// <b>It always succeeds.</b> Every reachable outcome — a malformed address, an address no Company
/// holds, a suspended Employee, one with no password to reset — returns the same success, because
/// the alternative is an unauthenticated endpoint that reports whether an address belongs to a
/// customer. That answer is worth having to an attacker and worth nothing to a legitimate user,
/// who already knows their own address.
/// <para>
/// <b>The Company is resolved before anything else.</b> Row-level security means the Employee
/// lookup finds nothing without a tenant in scope, and here the tenant is precisely what is
/// unknown. <see cref="ICredentialDirectory"/> answers that one question, the scope opens, and
/// every read after it is filtered normally.
/// </para>
/// <para>
/// <b>Outstanding tokens are invalidated first.</b> Without that, requesting a reset repeatedly
/// would accumulate live links, each a standing takeover credential for as long as it had left to
/// run — and the Employee would have no way to tell how many were outstanding.
/// </para>
/// </remarks>
public sealed class RequestPasswordResetCommandHandler(
    ICredentialDirectory directory,
    ITenantContext tenantContext,
    IEmployeeRepository employees,
    IEmployeeCredentialRepository credentials,
    IPasswordResetTokenRepository resetTokens,
    IPasswordResetTokenFactory tokenFactory,
    IPasswordResetNotifier notifier,
    IUnitOfWork unitOfWork,
    IOptions<PasswordResetOptions> options,
    TimeProvider timeProvider)
    : ICommandHandler<RequestPasswordResetCommand>
{
    public async Task<Result> HandleAsync(
        RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Email.TryCreate(command.Email, out var email))
        {
            return Result.Success();
        }

        var companyId = await directory.FindCompanyByEmailAsync(email, cancellationToken)
            .ConfigureAwait(false);

        if (companyId is null)
        {
            return Result.Success();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var employee = await employees.FindByEmailAsync(email, cancellationToken)
            .ConfigureAwait(false);

        // CanAuthenticate covers deleted, invited, suspended, and removed alike. Issuing a link to
        // an account that cannot sign in would produce a token that fails at the last step, after
        // the Employee has already been told to check their mail.
        if (employee is null || !employee.CanAuthenticate())
        {
            return Result.Success();
        }

        // Checked here rather than at completion. FR-AUTH-012 resets a forgotten password, and an
        // Employee who authenticates only through a federated identity — or whose Company has
        // disabled password authentication (FR-AUTH-004) — has none to forget. Issuing anyway
        // would create a token whose redemption has nothing to write to.
        if (!await credentials.ExistsForAsync(employee.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();

        await resetTokens
            .InvalidateOutstandingForEmployeeAsync(employee.Id, now, cancellationToken)
            .ConfigureAwait(false);

        var issued = tokenFactory.Issue();
        var expiresAtUtc = now.AddMinutes(options.Value.LifetimeMinutes);

        resetTokens.Add(PasswordResetToken.Issue(
            employee.CompanyId,
            employee.Id,
            issued.Hash,
            now,
            expiresAtUtc,
            command.IpAddress));

        // The commit for the tracked aggregate. The invalidation above is a set-based statement of
        // its own, ordered first because the safe partial outcome is the one where old links are
        // dead and the new one never appeared — the Employee simply asks again. Both become one
        // transaction when the ADR-0012 pipeline wraps the handler.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // After the commit, deliberately. A message carrying a token that was rolled back is a
        // link that fails for a legitimate Employee, and mail cannot be unsent. The reverse
        // failure — committed but undelivered — is recoverable: they can ask again.
        await notifier
            .SendAsync(employee.Email, employee.Id, issued.Token, expiresAtUtc, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }
}
