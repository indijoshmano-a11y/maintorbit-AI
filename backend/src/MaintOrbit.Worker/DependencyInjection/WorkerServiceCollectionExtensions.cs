using MaintOrbit.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MaintOrbit.Worker.DependencyInjection;

/// <summary>
/// The Worker's composition root.
/// </summary>
/// <remarks>
/// <b>Separate from the API's, sharing the layers beneath it.</b> ADR-0014 puts it exactly this
/// way — "the same libraries as the API host; a distinct entry point, not a distinct solution".
/// The two hosts compose the same Application and Infrastructure registrations and then diverge:
/// the API adds endpoints, authentication, and CORS; the Worker adds a scheduled service and
/// nothing else.
/// <para>
/// Nothing in this file registers an HTTP concern, and that is what makes DP-001's separation
/// real rather than nominal. A Worker that quietly carried the API's middleware would be a second
/// API host that happens not to listen.
/// </para>
/// </remarks>
internal static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the Worker runs.
    /// </summary>
    /// <remarks>
    /// <b>The connection string is read from the Worker's own configuration.</b> It binds the same
    /// <c>Persistence</c> section name, but from this process's settings and environment — so a
    /// deployment gives the Worker its own pool size, its own timeouts, and if required its own
    /// role, without touching the API. NFR-PERF-001 is the reason: batch work must not compete with
    /// the Gateway for connection-pool capacity, and it cannot compete for a pool it does not share.
    /// </remarks>
    public static IServiceCollection AddWorker(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // AddInfrastructureMaintenance, not AddInfrastructure — and not AddApplication at all.
        //
        // The full infrastructure root validates a JWT signing key and an encryption data key on
        // start, both C4 material this process has no use for. Composing it would mean shipping the
        // platform's token signing key to a second container to satisfy a validator for a feature
        // the Worker does not have, which is the opposite of P-4. The application root registers
        // command handlers nothing here dispatches.
        //
        // When the second job class arrives and needs the database through EF, this is the line
        // that grows — deliberately, and by the smallest amount that job requires.
        services.AddInfrastructureMaintenance(configuration);

        // Registered as the concrete type as well, because the health surface reads the last
        // cycle's outcome from the running instance — and AddHostedService alone gives no handle
        // on it. Singleton in both registrations so there is exactly one.
        services.TryAddSingleton<AuditPartitionMaintenanceService>();
        services.AddHostedService(
            provider => provider.GetRequiredService<AuditPartitionMaintenanceService>());

        services.TryAddSingleton<WorkerHealth>();

        return services;
    }
}
