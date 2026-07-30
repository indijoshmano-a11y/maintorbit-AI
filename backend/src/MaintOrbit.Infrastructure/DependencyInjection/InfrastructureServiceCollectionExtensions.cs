using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
/// Only the clock is registered at this milestone. Persistence, caching, messaging, and
/// provider adapters arrive with the milestones that introduce them.
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

        return services;
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
