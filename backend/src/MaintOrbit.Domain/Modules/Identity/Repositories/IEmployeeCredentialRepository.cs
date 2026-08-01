using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>
/// Access to <see cref="EmployeeCredential"/> aggregates.
/// </summary>
/// <remarks>
/// Separate from <see cref="IEmployeeRepository"/> because the aggregates are separate, and the
/// separation is the C4 control from 11.3: reaching a password hash must be a deliberate act, not
/// a side effect of loading an Employee.
/// </remarks>
public interface IEmployeeCredentialRepository
{
    /// <summary>
    /// Whether the Employee already has a credential.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="bool"/> rather than the credential. The caller asking this question —
    /// invitation acceptance — needs to know only whether one exists, and loading the aggregate to
    /// answer that would pull a hash into memory for no reason.
    /// </remarks>
    Task<bool> ExistsForAsync(EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the Employee's credential, or <see langword="null"/> if they have none.
    /// </summary>
    /// <remarks>
    /// The one path that deliberately pulls C4 material into memory, because verification cannot
    /// happen anywhere else. Absent is an ordinary outcome, not an error: a federated-only
    /// Employee has no password, and FR-AUTH-004 lets a Company disable password authentication
    /// entirely.
    /// </remarks>
    Task<EmployeeCredential?> FindForAsync(EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>Adds a new credential to the unit of work.</summary>
    void Add(EmployeeCredential credential);
}
