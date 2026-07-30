namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Registration seam for the API host.
/// </summary>
/// <remarks>
/// The outermost layer. It composes host-level concerns — configuration today; middleware,
/// endpoints, authentication, hubs, and health checks as those milestones land — and is
/// the only layer permitted to know about transport.
/// </remarks>
public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers API host services.
    /// </summary>
    public static IServiceCollection AddApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationConfiguration(configuration);

        return services;
    }
}
