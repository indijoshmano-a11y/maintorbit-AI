using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Validates a token's session against stored state.
/// </summary>
/// <remarks>
/// Runs inside the tenant scope the caller has already opened from the token's Company claim, so
/// row-level security applies: a session belonging to another Company is invisible here and is
/// refused as though it did not exist.
/// <para>
/// <b>The idle window is the Company's</b> (FR-AUTH-007), not the deployment's. It has to be the
/// same one the device list applies and the same one recording activity resets — three readers of
/// one window, and any disagreement means a session shown as live is refused, or one shown as gone
/// still works.
/// </para>
/// </remarks>
internal sealed class SessionValidator(
    ISessionRepository sessions,
    IAuthenticationPolicyProvider policies,
    TimeProvider timeProvider)
    : ISessionValidator
{
    /// <summary>
    /// The single answer every session failure produces.
    /// </summary>
    /// <remarks>
    /// §6.2 gives <c>session_revoked</c> for "session or family revoked; re-authenticate". Missing,
    /// revoked, idled out, and past its absolute lifetime all collapse to it: the caller holds a
    /// validly signed token, so telling them their own session ended reveals nothing about anyone
    /// else — and distinguishing the four would say which of them is worth retrying.
    /// </remarks>
    private static Result Rejected() =>
        Result.Failure(new Error("session_revoked", "The session is no longer valid."));

    /// <inheritdoc />
    public async Task<Result> ValidateAsync(
        SessionId sessionId,
        EmployeeId employeeId,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            // Absent, or belonging to another Company — row-level security makes those the same
            // observation, which is the correct one.
            return Rejected();
        }

        if (session.EmployeeId != employeeId || session.CompanyId != companyId)
        {
            // The token names a session that belongs to someone else. The signature was valid, so
            // this is not a forgery — it is a token issued against the wrong session, and
            // establishing a tenant context from it would cross a Company boundary.
            return Rejected();
        }

        // Read after the session is confirmed to belong to this Company, so the policy consulted
        // is that Company's and the lookup runs under a tenant scope that already matched.
        var policy = await policies.GetAsync(companyId, cancellationToken).ConfigureAwait(false);

        var idleTimeout = TimeSpan.FromMinutes(policy.IdleTimeoutMinutes);

        // Covers all three stored conditions at once: revoked, past the absolute lifetime, and
        // beyond the idle window.
        return session.IsActive(timeProvider.GetUtcNow(), idleTimeout)
            ? Result.Success()
            : Rejected();
    }
}
