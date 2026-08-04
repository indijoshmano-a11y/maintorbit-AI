using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Notifications;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Modules.Identity.Commands.EmailVerification;

/// <summary>
/// Asks for a verification link to be sent to the caller's own address (FR-AUTH-013).
/// </summary>
/// <remarks>
/// It carries nothing. The Employee comes from the validated access token, never from the request
/// — a field naming one would let a caller send verification links to other people's addresses,
/// and a link is a message somebody receives whether they asked for it or not.
/// </remarks>
public sealed record RequestEmailVerificationCommand : ICommand;

/// <summary>
/// Redeems a verification link (FR-AUTH-013).
/// </summary>
/// <remarks>
/// Unauthenticated by necessity: the link arrives by email and is opened in whatever browser the
/// Employee happens to be using. Requiring a session would make verification reachable only by
/// people who are already signed in — which, since verification gates activation, is close to
/// nobody.
/// </remarks>
/// <param name="Token">The token from the emailed link.</param>
public sealed record VerifyEmailCommand(string? Token) : ICommand;

/// <summary>
/// Issues a verification token for the authenticated Employee's address.
/// </summary>
/// <remarks>
/// <b>Outstanding links are invalidated first.</b> Without that, asking repeatedly would
/// accumulate live links — each one a standing proof of an address the Employee may since have
/// lost access to, and none of them distinguishable from the newest.
/// <para>
/// An address that is already verified may be verified again. Re-proving control is harmless and
/// occasionally necessary — a re-verification campaign after a provider incident, for instance —
/// and refusing would make the endpoint's behaviour depend on state the caller cannot see.
/// </para>
/// </remarks>
public sealed class RequestEmailVerificationCommandHandler(
    ICurrentIdentity currentIdentity,
    IEmployeeRepository employees,
    IEmailVerificationTokenRepository verifications,
    IEmailVerificationTokenFactory tokenFactory,
    IEmailVerificationNotifier notifier,
    IUnitOfWork unitOfWork,
    IOptions<EmailVerificationOptions> options,
    TimeProvider timeProvider)
    : ICommandHandler<RequestEmailVerificationCommand>
{
    public async Task<Result> HandleAsync(
        RequestEmailVerificationCommand command, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();

        var employee = await employees.FindAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            // A validated token for a row that is not visible. §6.2 makes absent and
            // not-visible-to-you the same answer.
            return Result.Failure(Error.NotFound("No such Employee."));
        }

        var now = timeProvider.GetUtcNow();

        await verifications
            .InvalidateOutstandingForEmployeeAsync(employeeId, now, cancellationToken)
            .ConfigureAwait(false);

        var issued = tokenFactory.Issue();
        var expiresAtUtc = now.AddMinutes(options.Value.LifetimeMinutes);

        verifications.Add(EmailVerificationToken.Issue(
            employee.CompanyId,
            employeeId,
            // The address as it is now. If it changes before the link is opened, redemption will
            // refuse — which is the whole reason the token records one.
            employee.Email,
            issued.Hash,
            now,
            expiresAtUtc));

        // The commit for the tracked aggregate. The invalidation above is its own statement,
        // ordered first because the safe partial outcome is the one where old links are dead and
        // the new one never appeared — the Employee simply asks again.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // After the commit, deliberately. A message carrying a token that was rolled back is a
        // link that fails for a legitimate Employee, and mail cannot be unsent.
        await notifier
            .SendAsync(employee.Email, employeeId, issued.Token, expiresAtUtc, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>
/// Redeems a verification token and records that the address is proved (FR-AUTH-013).
/// </summary>
/// <remarks>
/// <b>Every failure is the same failure.</b> An unknown token, an expired one, a replayed one, a
/// superseded one, and one issued for an address that has since changed all return
/// <c>authentication_failed</c> with one description. Distinguishing them would tell whoever is
/// probing which of their guesses was a real token, and §6.2 already treats "absent" and "not
/// visible to you" as one answer for the same reason.
/// <para>
/// <b>The Company is resolved before anything else.</b> Row-level security means the token lookup
/// finds nothing without a tenant in scope, and a link opened from an email carries a token and
/// nothing else. <see cref="ICredentialDirectory"/> answers that one question, the scope opens,
/// and every read after it is filtered normally.
/// </para>
/// </remarks>
public sealed class VerifyEmailCommandHandler(
    ICredentialDirectory directory,
    ITenantContext tenantContext,
    IEmailVerificationTokenRepository verifications,
    IEmailVerificationTokenFactory tokenFactory,
    IEmployeeRepository employees,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<VerifyEmailCommand>
{
    private static Result Rejected() =>
        Result.Failure(
            Error.AuthenticationFailed("The verification link is invalid or has expired."));

    public async Task<Result> HandleAsync(
        VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.Token))
        {
            return Rejected();
        }

        var hash = tokenFactory.Hash(command.Token);

        var companyId = await directory
            .FindCompanyByEmailVerificationTokenAsync(hash, cancellationToken)
            .ConfigureAwait(false);

        if (companyId is null)
        {
            return Rejected();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var verification = await verifications.FindByHashAsync(hash, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();

        // Expiry and supersession are checked before consumption, so a lapsed token is left as it
        // was rather than stamped as redeemed — a row that says "used" about a link nobody used is
        // a misleading record.
        if (verification is null || !verification.IsRedeemable(now))
        {
            return Rejected();
        }

        var employee = await employees.FindAsync(verification.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            return Rejected();
        }

        // The address must still be the one the token was issued for. Otherwise a link sent to an
        // old address would verify whatever replaced it — proof transferred to something that
        // never earned it.
        if (!verification.Matches(employee.Email))
        {
            // Spent anyway. The link is for an address this Employee no longer uses, and leaving
            // it live would keep a credential for a superseded address in circulation.
            verification.Invalidate(now);

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Rejected();
        }

        // The replay gate. Returns false when the token was consumed between the check above and
        // here, which is the concurrent-redemption race; the unique index on the hash and the row
        // version close the rest of it.
        if (!verification.TryConsume(now))
        {
            return Rejected();
        }

        var verified = employee.VerifyEmail(now);

        if (verified.IsFailure)
        {
            // Removed between issuance and redemption. The token is spent either way — a link that
            // failed because the account was gone must not stay live for a second attempt.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Rejected();
        }

        // The single commit. The consumed token and the verified Employee become visible together;
        // a partial apply would leave either a spent link that verified nothing or a verified
        // address whose proof can be replayed.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
