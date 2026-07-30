using Microsoft.Extensions.DependencyInjection;

namespace MaintOrbit.Application.DependencyInjection;

/// <summary>
/// Registration seam for the application layer.
/// </summary>
/// <remarks>
/// Every layer owns its own registration and exposes exactly one entry point. The
/// composition root calls these in dependency order and knows nothing about what each
/// layer contains — which is what keeps registration from leaking upward into
/// <c>Program.cs</c> as the system grows.
/// <para>
/// This layer registers nothing yet. Command and query handlers, validators, the
/// behaviour pipeline, and mapping configuration are registered here as the milestones
/// that introduce them land. The seam exists now so that later milestones extend a
/// stable contract rather than reshaping the composition root.
/// </para>
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers application-layer services.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
