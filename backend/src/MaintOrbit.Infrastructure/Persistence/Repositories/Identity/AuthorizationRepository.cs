using MaintOrbit.Domain.Modules.Identity.Entities;
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
    public void Add(EmployeeRole assignment) => context.EmployeeRoles.Add(assignment);
}
