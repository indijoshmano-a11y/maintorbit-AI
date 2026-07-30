using MaintOrbit.Api.Configuration;
using Microsoft.Extensions.Options;
using AspNetCoreHealthCheckOptions = Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions;

namespace MaintOrbit.Api.HealthChecks;

/// <summary>
/// Maps the liveness and readiness endpoints.
/// </summary>
/// <remarks>
/// Two endpoints, not one, because they answer different questions and a wrong answer to
/// either is costly in a different way (NFR-OBS-005, ADR-0018 §4).
/// <list type="bullet">
/// <item><description>
/// <b>Liveness</b> — "is this process alive". Failing it gets the container killed and
/// restarted, so it must depend on nothing external.
/// </description></item>
/// <item><description>
/// <b>Readiness</b> — "should traffic be routed here". Failing it removes the instance from
/// rotation, which is recoverable. Rolling deployment gates return-to-rotation on this, so a
/// readiness check that passes too early drops requests, and one that never passes stalls
/// the deployment.
/// </description></item>
/// </list>
/// </remarks>
public static class HealthCheckEndpointExtensions
{
    /// <summary>
    /// Maps health endpoints at their configured paths.
    /// </summary>
    /// <remarks>
    /// Paths come from <see cref="HealthCheckOptions"/> rather than being written here.
    /// A validator already guarantees they differ, so the two endpoints cannot be configured
    /// to collide.
    /// </remarks>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckOptions>>().Value;

        if (!options.Enabled)
        {
            return endpoints;
        }

        // Liveness runs no checks at all. Reaching the endpoint is the answer: if the process
        // can accept a connection and route a request, it is alive. Adding a dependency probe
        // here is the single most common way to turn a database blip into a cluster-wide
        // restart storm.
        endpoints.MapHealthChecks(options.LivenessPath, new AspNetCoreHealthCheckOptions
        {
            Predicate = static _ => false
        });

        // Readiness runs everything tagged Ready. Nothing carries that tag yet — no
        // persistence, cache, or transport exists — so the endpoint currently reports healthy
        // as soon as the host is up. That is correct rather than provisional: an instance with
        // no dependencies genuinely is ready. Each dependency adds its own tagged check with
        // the milestone that introduces it.
        endpoints.MapHealthChecks(options.ReadinessPath, new AspNetCoreHealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains(HealthCheckTags.Ready)
        });

        return endpoints;
    }
}
