using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Caching;

/// <summary>
/// What runs when no cache is configured: nothing is stored, every request resolves.
/// </summary>
/// <remarks>
/// The successor to <c>NoPermissionCache</c>, and the difference is not cosmetic. That type meant
/// "Redis does not exist yet"; this one means "this deployment has chosen not to cache", which is
/// a supported configuration with a defensible reason — every role change takes effect on the next
/// request rather than within <see cref="PermissionCacheOptions.TimeToLiveSeconds"/>.
/// <para>
/// <b>Still not an in-process dictionary</b>, and that remains the point. A per-host cache is
/// wrong in a way that only appears with more than one host: an invalidation on one leaves the
/// others serving a revoked role until their own copy lapses. Storing nothing is slower and always
/// correct; the cost shows up in latency rather than in stale grants.
/// </para>
/// </remarks>
internal sealed class DisabledPermissionCache : IPermissionCache
{
    /// <inheritdoc />
    public Task<EmployeePermissions?> GetAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult<EmployeePermissions?>(null);

    /// <inheritdoc />
    public Task SetAsync(
        EmployeeId employeeId,
        CompanyId companyId,
        EmployeePermissions permissions,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Succeeds rather than throwing. There is no entry to fail to remove, so a caller invalidating
    /// after a role change is correct and complete here — the guarantee it wanted already holds.
    /// </remarks>
    public Task InvalidateAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
