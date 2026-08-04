using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
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

    /// <summary>
    /// One assignment by identifier, or <see langword="null"/> if none is visible.
    /// </summary>
    /// <remarks>
    /// "Visible" is doing work: an assignment belonging to another Company is filtered by
    /// row-level security and is indistinguishable from one that does not exist. §6.2 makes them
    /// the same answer for exactly that reason — a <c>403</c> here would confirm the row exists.
    /// <para>
    /// Tracked, unlike <see cref="FindRolesForAsync"/>: the caller removes what it finds.
    /// </para>
    /// </remarks>
    Task<EmployeeRole?> FindAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the Employee already holds this role at this exact scope.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="bool"/> rather than the row, because the caller only needs to know
    /// whether to refuse. <c>ux_employee_roles_employee_id_role_code_scope</c> enforces this in the
    /// database regardless; asking first turns a unique-violation exception into an ordinary
    /// conflict result, and the constraint remains the guarantee under concurrency.
    /// </remarks>
    Task<bool> AssignmentExistsAsync(
        EmployeeId employeeId,
        RoleCode roleCode,
        PermissionScope scopeType,
        Guid? scopeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether a role is defined.
    /// </summary>
    /// <remarks>
    /// Platform-wide reference data, so no tenant filter.
    /// <para>
    /// <c>fk_employee_roles_role_definitions_role_code</c> already enforces this — both tables are
    /// in the identity schema, so DB-P2 does not forbid the key. Asking first turns a
    /// foreign-key violation into an ordinary <c>not_found</c> the caller can act on, and the
    /// constraint stays the guarantee.
    /// </para>
    /// </remarks>
    Task<bool> RoleExistsAsync(RoleCode roleCode, CancellationToken cancellationToken);

    /// <summary>Adds a role assignment to the unit of work.</summary>
    void Add(EmployeeRole assignment);

    /// <summary>
    /// Removes a role assignment from the unit of work.
    /// </summary>
    /// <remarks>
    /// A hard delete, and deliberately. An assignment is not a record of something that happened —
    /// it is the statement that someone currently holds a role, and a tombstoned one would have to
    /// be excluded by every reader of <see cref="FindRolesForAsync"/>, which is the filter that
    /// eventually gets forgotten. What happened is the audit trail's job, and that module does not
    /// exist yet.
    /// </remarks>
    void Remove(EmployeeRole assignment);
}
