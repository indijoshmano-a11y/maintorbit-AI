using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="PasswordResetToken"/> aggregates.</summary>
/// <remarks>
/// Tenant filtering is absent for the same reason as every other repository here: row-level
/// security applies it below the application layer (ADR-0005), and a second discretionary copy is
/// the one that gets forgotten.
/// </remarks>
public interface IPasswordResetTokenRepository
{
    /// <summary>
    /// Finds a reset request by its token hash, or <see langword="null"/> if none is visible.
    /// </summary>
    /// <remarks>
    /// Consumed, invalidated, and expired rows are all returned. Filtering them here would turn a
    /// replayed link into "unknown token", which is the same answer as a typo and tells the
    /// handler nothing — recognising a spent token is what makes it single-use.
    /// </remarks>
    Task<PasswordResetToken?> FindByHashAsync(
        PasswordResetTokenHash hash, CancellationToken cancellationToken);

    /// <summary>Adds a new reset request to the unit of work.</summary>
    void Add(PasswordResetToken token);

    /// <summary>
    /// Invalidates every outstanding reset token belonging to an Employee, except one.
    /// </summary>
    /// <remarks>
    /// Called when a request supersedes earlier ones and again when a reset completes, so at most
    /// one live token exists at a time. Set-based: an Employee who requested a reset repeatedly
    /// has an unbounded number of rows, and loading each to write one column would read them all
    /// into memory. Consumed rows are left alone — they are spent, and overwriting them would lose
    /// the record of a link that was actually used.
    /// <para>
    /// <b><paramref name="excluding"/> is not a convenience.</b> This runs as its own statement,
    /// so it sees the database rather than the change tracker — and on the completion path the
    /// token being redeemed has been marked consumed in memory but not yet written. Without the
    /// exclusion the sweep finds it still outstanding and stamps it invalidated, leaving a row
    /// that claims to be both used and superseded.
    /// </para>
    /// </remarks>
    Task<int> InvalidateOutstandingForEmployeeAsync(
        EmployeeId employeeId,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken,
        PasswordResetTokenId? excluding = null);
}
