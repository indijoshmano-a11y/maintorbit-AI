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
