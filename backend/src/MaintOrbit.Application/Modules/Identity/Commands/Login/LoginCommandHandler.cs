using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Modules.Identity.Commands.Login;

/// <summary>
/// Verifies an Employee's email address and password.
/// </summary>
/// <remarks>
/// <b>Every failure returns the same error, and takes about the same time.</b> §6.2 defines one
/// <c>authentication_failed</c> category for "invalid or missing credential", and threat I-13
/// requires uniform responses — a malformed address, an unknown one, an Employee with no password,
/// a suspended account, a locked account, and a wrong password are indistinguishable from outside.
/// Distinguishing any of them hands an attacker a way to find which addresses are worth attacking.
/// <para>
/// Uniformity in duration takes deliberate work: Argon2id is expensive, so returning before
/// reaching it is measurably faster. Each path that has nothing to verify instead verifies against
/// <see cref="IDecoyPasswordHash"/> and discards the answer, so the miss costs what the hit costs.
/// </para>
/// <para>
/// <b>It writes exactly one thing: the failed-attempt counter (FR-AUTH-011).</b> No session, no
/// token, no last-login timestamp. The counter is committed here rather than by the caller
/// because a failed attempt has no other commit point — sign-in returns as soon as this rejects,
/// and a count that only survived a successful login would never lock anything.
/// </para>
/// <para>
/// <b>A locked account is not distinguishable from a wrong password.</b> It rejects with the same
/// error, and it still pays for a decoy verification — skipping the hash would make a locked
/// account answer measurably faster, which is an oracle for exactly the accounts an attacker has
/// been probing. That deliberately declines the saving T-5 would suggest, because the saving is
/// observable.
/// </para>
/// <para>
/// <b>The Employee is resolved within the active tenant context.</b> Row-level security applies to
/// the lookup, so a login attempt finds nothing unless a Company is already in scope. How a login
/// request determines which Company it is for is not documented — the email uniqueness index is
/// per-Company, and 04-tenant-security §3.4's operative table of paths that legitimately span
/// Companies does not include authentication. This handler does not invent a thirteenth.
/// </para>
/// </remarks>
public sealed class LoginCommandHandler(
    IEmployeeRepository employees,
    IEmployeeCredentialRepository credentials,
    IPasswordHasher passwordHasher,
    IDecoyPasswordHash decoy,
    IAuthenticationPolicyProvider policies,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<LoginCommand, AuthenticationResult>
{
    /// <summary>The single answer every failure produces.</summary>
    private static Result<AuthenticationResult> Rejected() =>
        Result.Failure<AuthenticationResult>(
            Error.AuthenticationFailed("The email address or password is incorrect."));

    public async Task<Result<AuthenticationResult>> HandleAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.Password) || !Email.TryCreate(command.Email, out var email))
        {
            // The only path that returns without hashing. An absent password and a malformed
            // address are both malformed *requests* rather than failed attempts — no well-formed
            // submission reaches here, so the timing difference reveals nothing about any account.
            return Rejected();
        }

        var now = timeProvider.GetUtcNow();

        var employee = await employees.FindByEmailAsync(email, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null || !employee.CanAuthenticate())
        {
            // Unknown address, or an Employee who is invited, suspended, removed, or soft-deleted.
            // The decoy makes this cost what a real verification costs.
            return RejectAfterEqualWork(command.Password);
        }

        var credential = await credentials.FindForAsync(employee.Id, cancellationToken)
            .ConfigureAwait(false);

        if (credential is null || credential.IsLockedOut(now))
        {
            // No password set — a federated-only Employee, or a Company that disabled password
            // authentication (FR-AUTH-004) — or a lockout in force.
            //
            // A locked account records nothing. Counting attempts it never verified would let an
            // attacker extend somebody's lockout indefinitely by continuing to knock, which turns
            // the control into the denial-of-service vector 07-api-security T-3 warns about.
            return RejectAfterEqualWork(command.Password);
        }

        var verification = passwordHasher.Verify(credential.PasswordHash, command.Password);

        if (verification != PasswordVerificationResult.Success)
        {
            // Covers Failed and Unusable alike. A stored hash that will not parse is an
            // operational fault worth an alert, but it is not a distinction the caller may see —
            // it would confirm the account exists.
            await RecordFailureAsync(employee.CompanyId, credential, now, cancellationToken)
                .ConfigureAwait(false);

            return Rejected();
        }

        // A success is proof the holder is present, so the run of failures before it stops
        // counting. Without this the count would accumulate across weeks of ordinary typing
        // mistakes and lock an account that was never under attack.
        credential.RecordSuccessfulAttempt(now);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new AuthenticationResult(
            employee.Id,
            employee.CompanyId,
            // SD-010 reviews parameters annually, and a successful authentication is the only
            // moment the plaintext exists to re-derive from. Reported rather than acted on: this
            // milestone writes nothing, so the caller decides when to upgrade the stored hash.
            PasswordNeedsRehash: passwordHasher.NeedsRehash(credential.PasswordHash)));
    }

    /// <summary>
    /// Counts the failure and locks the account if it has reached the Company's threshold.
    /// </summary>
    /// <remarks>
    /// <b>Committed even though the request fails</b>, and that is the whole point: sign-in
    /// returns the moment this rejects, so a counter left uncommitted would reset itself on every
    /// attempt and lock nothing ever.
    /// <para>
    /// The threshold and duration are the Company's (FR-AUTH-011). Read here rather than passed
    /// in, because only the failure path needs them and only a real Employee has a Company.
    /// </para>
    /// <para>
    /// Whether this attempt locked the account changes nothing the caller returns. The next
    /// attempt will be refused by the lockout check above, with the same error as every other
    /// failure — the Employee learns of it through the notification FR-AUTH-011 requires, which is
    /// not this milestone's to send.
    /// </para>
    /// </remarks>
    private async Task RecordFailureAsync(
        CompanyId companyId,
        EmployeeCredential credential,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(companyId, cancellationToken).ConfigureAwait(false);

        credential.RecordFailedAttempt(
            policy.MaximumFailedAttempts, TimeSpan.FromMinutes(policy.LockoutMinutes), now);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a verification that cannot succeed, then rejects.
    /// </summary>
    /// <remarks>
    /// The result is discarded deliberately — only the elapsed work matters. This is what stops
    /// response time from revealing whether an address belongs to an account.
    /// </remarks>
    private Result<AuthenticationResult> RejectAfterEqualWork(string password)
    {
        passwordHasher.Verify(decoy.Value, password);

        return Rejected();
    }
}
