using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Authorization;

/// <summary>
/// A cache that holds nothing.
/// </summary>
/// <remarks>
/// §3.6 resolves permissions from cache, and ADR-0006 makes that cache Redis — which is not built.
/// Rather than substitute an in-memory dictionary, this stores nothing and every request resolves
/// from the database.
/// <para>
/// That choice is deliberate. A per-process dictionary would be <b>wrong in a way that only
/// appears under load</b>: with more than one API host, a role change invalidated on one host
/// stays cached on the others, and FR-PERM-005's 60-second requirement fails silently on some
/// fraction of requests. Reading the database every time is slower and always correct, and it
/// makes the cost of the missing cache visible in latency rather than hidden in stale grants.
/// </para>
/// </remarks>
internal sealed class NoPermissionCache : IPermissionCache
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
    public Task InvalidateAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
