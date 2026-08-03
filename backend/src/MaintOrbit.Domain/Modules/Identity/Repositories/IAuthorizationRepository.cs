using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>
/// Access to role assignments and the permission catalogue.
/// </summary>
/// <remarks>
/// One repository rather than four, because the four tables are only ever read together: resolving
/// what an Employee may do is a single join from <c>employee_roles</c> through
/// <c>role_permissions</c>. Four repositories would make the caller assemble that join, which is
/// how a resolution ends up written twice and differing.
/// </remarks>
public interface IAuthorizationRepository
{
    /// <summary>
    /// The roles an Employee holds, with their scopes.
    /// </summary>
    /// <remarks>
    /// Tenant-scoped: row-level security filters <c>employee_roles</c>, so grants held in another
    /// Company are invisible.
    /// </remarks>
    Task<IReadOnlyList<EmployeeRole>> FindRolesForAsync(
        EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// The permissions each of the named roles grants.
    /// </summary>
    /// <remarks>
    /// Platform-wide reference data — role definitions and their grants are the same for every
    /// Company, so this carries no tenant filter and needs none.
    /// </remarks>
    Task<IReadOnlyList<RolePermission>> FindPermissionsForRolesAsync(
        IReadOnlyCollection<RoleCode> roleCodes, CancellationToken cancellationToken);

    /// <summary>Adds a role assignment to the unit of work.</summary>
    void Add(EmployeeRole assignment);
}
