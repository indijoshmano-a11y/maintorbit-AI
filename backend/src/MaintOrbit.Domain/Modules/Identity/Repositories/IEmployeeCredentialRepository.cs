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

    /// <summary>Adds a new credential to the unit of work.</summary>
    void Add(EmployeeCredential credential);
}
