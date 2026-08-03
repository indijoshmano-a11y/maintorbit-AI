using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Authorization;

/// <summary>
/// Resolves an Employee's permissions from their role assignments.
/// </summary>
/// <remarks>
/// Two reads: the Employee's assignments, then the permissions those roles grant. Split rather than
/// joined because the second half is platform-wide reference data that a cache can hold across
/// every Company, while the first is tenant-scoped and cannot.
/// <para>
/// Multiple roles union. §3.4 permits an Employee to hold several and states there is no
/// hierarchy — so holding two roles grants both sets, and a permission granted twice at different
/// scopes keeps both, because a Company-wide grant and a Team-scoped one are not the same grant.
/// </para>
/// </remarks>
internal sealed class PermissionService(
    IAuthorizationRepository repository,
    IPermissionCache cache)
    : IPermissionService
{
    /// <inheritdoc />
    public async Task<EmployeePermissions> ResolveAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(employeeId, companyId, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            return cached;
        }

        var assignments = await repository.FindRolesForAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (assignments.Count == 0)
        {
            // Deny by default: an Employee with no roles holds nothing. Cached like any other
            // answer, so a directory of role-less Employees does not read the database repeatedly.
            await cache.SetAsync(employeeId, companyId, EmployeePermissions.None, cancellationToken)
                .ConfigureAwait(false);

            return EmployeePermissions.None;
        }

        var roleCodes = assignments.Select(static role => role.RoleCode).Distinct().ToList();

        var grants = await repository
            .FindPermissionsForRolesAsync(roleCodes, cancellationToken).ConfigureAwait(false);

        var byRole = grants
            .GroupBy(static grant => grant.RoleCode)
            .ToDictionary(static group => group.Key, static group => group.ToList());

        var resolved = new List<GrantedPermission>();

        foreach (var assignment in assignments)
        {
            if (!byRole.TryGetValue(assignment.RoleCode, out var permissions))
            {
                // A role with no grants. Possible while a role is being composed (FR-PERM-006) and
                // harmless: it contributes nothing, which is what deny-by-default already assumes.
                continue;
            }

            // The assignment's scope applies to every permission the role carries — §3.5 evaluates
            // scope and permission together, so a Team-scoped assignment narrows all of them.
            resolved.AddRange(permissions.Select(permission =>
                new GrantedPermission(permission.PermissionCode, assignment.ScopeType, assignment.ScopeId)));
        }

        var result = new EmployeePermissions(resolved);

        await cache.SetAsync(employeeId, companyId, result, cancellationToken).ConfigureAwait(false);

        return result;
    }
}
