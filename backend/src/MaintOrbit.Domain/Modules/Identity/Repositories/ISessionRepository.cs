using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="Session"/> aggregates.</summary>
/// <remarks>
/// Tenant filtering is absent for the same reason as every other repository here: row-level
/// security applies it below the application layer, and a second discretionary copy is the one
/// that gets forgotten.
/// </remarks>
public interface ISessionRepository
{
    /// <summary>Finds a session by identifier, or <see langword="null"/> if none is visible.</summary>
    Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken);

    /// <summary>Adds a new session to the unit of work.</summary>
    void Add(Session session);

    /// <summary>
    /// Every unrevoked session an Employee holds, newest first.
    /// </summary>
    /// <remarks>
    /// <b>Unrevoked, not active.</b> Whether a session is still within its idle window depends on
    /// the Company's policy (FR-AUTH-007), which this layer does not hold — so the repository
    /// returns what is not explicitly ended and the caller applies the timers. Filtering here with
    /// a guessed window would show a different list than the one the session validator honours.
    /// <para>
    /// Tenant filtering is absent for the same reason as every other read here: row-level security
    /// applies it below the application layer.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Session>> ListUnrevokedForEmployeeAsync(
        EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every unrevoked session an Employee holds except one.
    /// </summary>
    /// <remarks>
    /// §3.5's "Employee terminates all others — all except current". The exclusion is the whole
    /// point: an Employee clearing their other devices should not have to sign in again on the one
    /// they are using, and a caller that had to re-authenticate afterwards would learn to avoid
    /// the feature.
    /// <para>
    /// Set-based: an Employee may hold many sessions, and loading each to write one column would
    /// read an unbounded set into memory.
    /// </para>
    /// </remarks>
    Task<int> RevokeAllForEmployeeExceptAsync(
        EmployeeId employeeId,
        SessionId except,
        SessionRevocationReason reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every unrevoked session belonging to an Employee.
    /// </summary>
    /// <remarks>
    /// The bulk paths §3.5 enumerates — password change, lockout, deprovisioning, an administrator
    /// terminating sessions — all end every session at once. Expressed as one operation because
    /// loading each session to revoke it would read an unbounded set into memory to write a single
    /// column on each.
    /// </remarks>
    Task<int> RevokeAllForEmployeeAsync(
        EmployeeId employeeId,
        SessionRevocationReason reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);
}
