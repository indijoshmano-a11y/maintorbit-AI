using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Modules.Identity.Commands.CompletePasswordReset;

/// <summary>
/// Redeems a reset token, replaces the password, and ends every session (FR-AUTH-012,
/// NFR-SEC-017).
/// </summary>
/// <remarks>
/// <b>Every failure is the same failure.</b> An unknown token, an expired one, a replayed one, a
/// superseded one, and a token for an account that can no longer sign in all return
/// <c>authentication_failed</c> with one description. Distinguishing them would tell whoever is
/// probing which of their guesses was a real token that had merely lapsed, and §6.2 already treats
/// "absent" and "not visible to you" as one answer for the same reason.
/// <para>
/// <b>Ordering is load-bearing.</b> The token is checked and consumed before the password is
/// hashed: Argon2id at production parameters costs real memory and CPU, and hashing for a request
/// that cannot succeed hands an attacker a way to spend the server's resources by replaying a
/// spent link (T-5).
/// </para>
/// <para>
/// <b>Sessions end in the same transaction.</b> NFR-SEC-017 requires session tokens to be
/// invalidated on password change. A reset is the case that matters most — the plausible reason
/// for one is that somebody else holds the old password, and possibly a live session with it.
/// </para>
/// </remarks>
public sealed class CompletePasswordResetCommandHandler(
    ICredentialDirectory directory,
    ITenantContext tenantContext,
    IPasswordResetTokenRepository resetTokens,
    IPasswordResetTokenFactory tokenFactory,
    IEmployeeRepository employees,
    IEmployeeCredentialRepository credentials,
    ISessionRepository sessions,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<CompletePasswordResetCommand>
{
    private static Result Rejected() =>
        Result.Failure(
            Error.AuthenticationFailed("The reset link is invalid or has expired."));

    public async Task<Result> HandleAsync(
        CompletePasswordResetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.Token))
        {
            return Rejected();
        }

        if (string.IsNullOrEmpty(command.NewPassword))
        {
            // The one failure that is not uniform, and it says nothing about any account: the
            // request is missing a field. The strength policy (FR-AUTH-002) belongs in a
            // validation behaviour ahead of this handler; what is enforced here is the floor
            // below which the hasher itself refuses to operate.
            return Result.Failure(Error.Validation("A new password is required."));
        }

        var hash = tokenFactory.Hash(command.Token);

        var companyId = await directory
            .FindCompanyByPasswordResetTokenAsync(hash, cancellationToken)
            .ConfigureAwait(false);

        if (companyId is null)
        {
            return Rejected();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var resetToken = await resetTokens.FindByHashAsync(hash, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();

        // Expiry and invalidation are checked before consumption so that a lapsed token is left as
        // it was rather than stamped as redeemed — a row that says "used" about a link nobody used
        // is a misleading record of an account's recovery history.
        if (resetToken is null || !resetToken.IsRedeemable(now))
        {
            return Rejected();
        }

        // The replay gate. Returns false when the token was consumed between the check above and
        // here, which is the concurrent-redemption race; the unique index on the hash and the row
        // version close the rest of it.
        if (!resetToken.TryConsume(now))
        {
            return Rejected();
        }

        var employee = await employees.FindAsync(resetToken.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null || !employee.CanAuthenticate())
        {
            // Suspended or removed between the request and the redemption. The token is spent
            // either way — a link that failed because the account was disabled must not stay live
            // for a second attempt.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Rejected();
        }

        var credential = await credentials.FindForAsync(employee.Id, cancellationToken)
            .ConfigureAwait(false);

        if (credential is null)
        {
            // Unreachable through the documented path — the request handler declines to issue a
            // token for an Employee with no credential — and refused rather than silently
            // establishing one, because a reset that can create a first password would be an
            // enrolment path with no invitation behind it.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Rejected();
        }

        credential.ChangePassword(
            passwordHasher.Hash(command.NewPassword),
            PasswordAlgorithm.Argon2id,
            passwordHasher.CurrentVersion.Value,
            passwordHasher.CurrentParameters,
            now,
            // The Employee changed it themselves; a reset has no administrator behind it.
            changedBy: null);

        // NFR-SEC-017. Set-based, and it includes the requester's own sessions — there is no
        // "current" session in a reset, and an attacker holding one is the reason to do this.
        await sessions
            .RevokeAllForEmployeeAsync(
                employee.Id, SessionRevocationReason.PasswordChanged, now, cancellationToken)
            .ConfigureAwait(false);

        // Any other link that was outstanding dies with the password it would have replaced. The
        // one being redeemed is excluded: it is consumed in memory but not yet written, so the
        // sweep would otherwise find it outstanding and mark it superseded as well.
        await resetTokens
            .InvalidateOutstandingForEmployeeAsync(
                employee.Id, now, cancellationToken, excluding: resetToken.Id)
            .ConfigureAwait(false);

        // The single commit for the tracked aggregates — the consumed token and the new hash.
        // The two set-based sweeps above run as their own statements, which is why they are
        // ordered before it: each is safe to have applied on its own, and neither leaves the old
        // password usable. Both become one transaction when the ADR-0012 pipeline wraps the
        // handler; nothing here changes when it does.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
