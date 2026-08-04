using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="IAuthorizationRepository"/>.</summary>
internal sealed class AuthorizationRepository(MaintOrbitDbContext context) : IAuthorizationRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EmployeeRole>> FindRolesForAsync(
        EmployeeId employeeId, CancellationToken cancellationToken) =>
        // AsNoTracking: this is a read on the authorization path, run on every request that checks
        // a permission. Tracking would add change-detection cost for entities nothing mutates.
        await context.EmployeeRoles
            .AsNoTracking()
            .Where(role => role.EmployeeId == employeeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RolePermission>> FindPermissionsForRolesAsync(
        IReadOnlyCollection<RoleCode> roleCodes, CancellationToken cancellationToken)
    {
        if (roleCodes.Count == 0)
        {
            return [];
        }

        return await context.RolePermissions
            .AsNoTracking()
            .Where(grant => roleCodes.Contains(grant.RoleCode))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<EmployeeRole?> FindAssignmentAsync(
        Guid assignmentId, CancellationToken cancellationToken) =>
        // Tracked, unlike the resolution path: the caller removes what it finds, and an untracked
        // aggregate would be removed in memory and never written.
        context.EmployeeRoles.FirstOrDefaultAsync(
            assignment => assignment.Id == assignmentId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AssignmentExistsAsync(
        EmployeeId employeeId,
        RoleCode roleCode,
        PermissionScope scopeType,
        Guid? scopeId,
        CancellationToken cancellationToken) =>
        context.EmployeeRoles.AnyAsync(
            assignment =>
                assignment.EmployeeId == employeeId &&
                assignment.RoleCode == roleCode &&
                assignment.ScopeType == scopeType &&
                assignment.ScopeId == scopeId,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> RoleExistsAsync(RoleCode roleCode, CancellationToken cancellationToken) =>
        context.RoleDefinitions.AnyAsync(role => role.Code == roleCode, cancellationToken);

    /// <inheritdoc />
    public void Add(EmployeeRole assignment) => context.EmployeeRoles.Add(assignment);

    /// <inheritdoc />
    public void Remove(EmployeeRole assignment) => context.EmployeeRoles.Remove(assignment);
}
