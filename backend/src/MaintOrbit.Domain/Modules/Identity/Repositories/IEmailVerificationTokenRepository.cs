using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="EmailVerificationToken"/> aggregates.</summary>
/// <remarks>
/// Tenant filtering is absent for the same reason as every other repository here: row-level
/// security applies it below the application layer (ADR-0005), and a second discretionary copy is
/// the one that gets forgotten.
/// </remarks>
public interface IEmailVerificationTokenRepository
{
    /// <summary>
    /// Finds a verification by its token hash, or <see langword="null"/> if none is visible.
    /// </summary>
    /// <remarks>
    /// Consumed, invalidated, and expired rows are all returned. Filtering them here would turn a
    /// replayed link into "unknown token", which is the same answer as a typo and tells the
    /// handler nothing — recognising a spent token is what makes it single-use.
    /// </remarks>
    Task<EmailVerificationToken?> FindByHashAsync(
        EmailVerificationTokenHash hash, CancellationToken cancellationToken);

    /// <summary>Adds a new verification to the unit of work.</summary>
    void Add(EmailVerificationToken token);

    /// <summary>
    /// Invalidates every outstanding verification belonging to an Employee.
    /// </summary>
    /// <remarks>
    /// Called when a request supersedes earlier ones, so at most one live link exists at a time.
    /// Set-based: an Employee who asked repeatedly has an unbounded number of rows, and loading
    /// each to write one column would read them all into memory. Consumed rows are left alone —
    /// they are spent, and overwriting them would lose the record of a link that was used.
    /// </remarks>
    Task<int> InvalidateOutstandingForEmployeeAsync(
        EmployeeId employeeId,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken);
}
