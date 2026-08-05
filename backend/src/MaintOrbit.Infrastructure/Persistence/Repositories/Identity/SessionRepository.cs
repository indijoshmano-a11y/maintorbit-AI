using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="ISessionRepository"/>.</summary>
internal sealed class SessionRepository(MaintOrbitDbContext context) : ISessionRepository
{
    /// <inheritdoc />
    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        context.Sessions.FirstOrDefaultAsync(session => session.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Session session) => context.Sessions.Add(session);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> ListUnrevokedForEmployeeAsync(
        EmployeeId employeeId, CancellationToken cancellationToken) =>
        // AsNoTracking: a device list changes nothing. Ordered newest first because that is the
        // order the list is read in — the device somebody just signed in on is the one they are
        // looking for.
        await context.Sessions
            .AsNoTracking()
            .Where(session => session.EmployeeId == employeeId && session.RevokedAtUtc == null)
            .OrderByDescending(session => session.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<int> RevokeAllForEmployeeExceptAsync(
        EmployeeId employeeId,
        SessionId except,
        SessionRevocationReason reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        context.Sessions
            .Where(session =>
                session.EmployeeId == employeeId &&
                session.RevokedAtUtc == null &&
                session.Id != except)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAtUtc, revokedAtUtc)
                    .SetProperty(session => session.RevocationReason, reason)
                    .SetProperty(session => session.UpdatedAtUtc, revokedAtUtc),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeAllForEmployeeAsync(
        EmployeeId employeeId,
        SessionRevocationReason reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        // A set-based update. Loading every session to revoke it would read an unbounded set into
        // memory to write one column on each, and these sweeps run at exactly the moments that
        // matter — a password change, a lockout, a deprovisioning.
        //
        // Row-level security still applies: this updates only rows the tenant context can see.
        context.Sessions
            .Where(session => session.EmployeeId == employeeId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAtUtc, revokedAtUtc)
                    .SetProperty(session => session.RevocationReason, reason)
                    .SetProperty(session => session.UpdatedAtUtc, revokedAtUtc),
                cancellationToken);
}
