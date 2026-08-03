using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Authorization;

/// <summary>
/// Resolves the permissions an Employee holds.
/// </summary>
/// <remarks>
/// <b>From the database, never from the token.</b> FR-PERM-005 requires a role change to take
/// effect within 60 seconds, which a self-contained 15-minute access token cannot honour — and a
/// token carrying permissions is a stale authorization decision travelling around the network.
/// §3.6 resolves them per request instead.
/// <para>
/// The lookup is tenant-scoped like every other read: row-level security filters
/// <c>employee_roles</c>, so an Employee's grants in another Company are invisible even to a
/// resolver asked for them by identifier.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// Everything the Employee is permitted to do in the Company currently in scope.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="EmployeePermissions.None"/> rather than failing when the Employee holds
    /// no roles. Deny by default makes "no grants" an ordinary answer, not an error.
    /// </remarks>
    Task<EmployeePermissions> ResolveAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken);
}

/// <summary>
/// Caches resolved permissions between requests.
/// </summary>
/// <remarks>
/// A seam, not a policy. §3.6 resolves permissions "server-side per request from cache", and T-6
/// accepts a cache read per request as the cost of keeping them out of the token. The cache that
/// backs this is Redis (ADR-0006), which does not exist yet — so the registered implementation
/// holds nothing and every resolution reads the database.
/// <para>
/// <b>The entry lifetime is what makes FR-PERM-005 achievable.</b> A role change must take effect
/// within 60 seconds, so whatever implementation lands must bound entries below that and be
/// invalidated on assignment — the abstraction exists now so that requirement has somewhere to
/// live rather than being retrofitted through every call site.
/// </para>
/// </remarks>
public interface IPermissionCache
{
    /// <summary>The cached permissions, or <see langword="null"/> if none are cached.</summary>
    Task<EmployeePermissions?> GetAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken);

    /// <summary>Caches a resolution.</summary>
    Task SetAsync(
        EmployeeId employeeId,
        CompanyId companyId,
        EmployeePermissions permissions,
        CancellationToken cancellationToken);

    /// <summary>Drops an Employee's entry, so the next request resolves afresh.</summary>
    /// <remarks>Called when a role is assigned or removed — the other half of FR-PERM-005.</remarks>
    Task InvalidateAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken);
}
