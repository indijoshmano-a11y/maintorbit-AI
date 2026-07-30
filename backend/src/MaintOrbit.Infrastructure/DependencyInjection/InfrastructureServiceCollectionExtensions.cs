using MaintOrbit.Infrastructure.Telemetry;
using MaintOrbit.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaintOrbit.Infrastructure.DependencyInjection;

/// <summary>
/// Registration seam for the infrastructure layer.
/// </summary>
/// <remarks>
/// Infrastructure implements the ports declared in the application layer, so it is the
/// layer that supplies concrete adapters — persistence, caching, provider clients,
/// telemetry, and the system clock. Each is registered against its abstraction, never its
/// concrete type (DI-6), so that a caller cannot take a dependency on an implementation
/// detail even by accident.
/// <para>
/// The clock and the correlation accessor are registered at this milestone. Persistence,
/// caching, messaging, and provider adapters arrive with the milestones that introduce them.
/// </para>
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure-layer services.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddClock(services);
        AddCorrelation(services);

        return services;
    }

    /// <summary>
    /// Registers the ambient correlation identifier accessor.
    /// </summary>
    /// <remarks>
    /// Registered as a singleton because the accessor is stateless — the identifier it reads
    /// lives in the caller's execution context, not in the object (DI-3). That also makes it
    /// safe to inject into other singletons, which matters: a scoped registration here would
    /// make every logging component request-scoped by contagion, and would leave the Worker
    /// with no way to correlate at all.
    /// <para>
    /// Registered against <see cref="ICorrelationIdAccessor"/> (DI-6). The implementation is
    /// internal, so nothing outside this assembly can take a dependency on the
    /// <see cref="AsyncLocal{T}"/> mechanism even deliberately.
    /// </para>
    /// </remarks>
    private static void AddCorrelation(IServiceCollection services)
    {
        services.TryAddSingleton<ICorrelationIdAccessor, CorrelationIdAccessor>();
    }

    /// <summary>
    /// Registers the system clock.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider"/> is the time abstraction the codebase depends on.
    /// Coding standard U-3 forbids <c>DateTime.Now</c> and <c>DateTime.UtcNow</c> outside
    /// it, and architecture test AT-9 enforces that — which only works if there is
    /// something to inject instead.
    /// <para>
    /// Registered as a singleton because the clock holds no per-request state (DI-3), and
    /// against the abstract <see cref="TimeProvider"/> rather than a concrete type (DI-6)
    /// so that tests substitute a controllable clock without the code under test knowing.
    /// </para>
    /// </remarks>
    private static void AddClock(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
    }
}
