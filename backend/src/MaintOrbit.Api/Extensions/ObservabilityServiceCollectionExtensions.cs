using Microsoft.Extensions.Logging.Console;

namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Registration seam for the three observability signals.
/// </summary>
/// <remarks>
/// ADR-0020 commits to structured logs, OpenTelemetry traces and metrics, and a correlation
/// identifier returned to the caller. This milestone implements logging and correlation and
/// establishes named seams for the other two, so the milestone that brings instrumentation
/// extends a method that already exists rather than reopening the composition root.
/// <para>
/// No exporter is registered here. Exporters bind the deployment to a specific monitoring
/// vendor, which is exactly what NFR-OBS-007's "open format consumable by standard
/// monitoring systems" is written to avoid deciding early.
/// </para>
/// </remarks>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers logging, health checks, and the metrics and tracing seams.
    /// </summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddStructuredLogging();
        services.AddHealthChecks();
        services.AddMetricsInstrumentation();
        services.AddTracingInstrumentation();

        return services;
    }

    /// <summary>
    /// Configures logging so that structured output and correlation survive configuration.
    /// </summary>
    /// <remarks>
    /// Log levels and the console formatter come from the <c>Logging</c> section, which the
    /// host binds per environment — that is deliberately left to configuration. Two things
    /// are not, because they are correctness rather than preference:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Scopes are always included.</b> The correlation identifier travels in a logging
    /// scope, so a formatter configured with <c>IncludeScopes</c> disabled drops it from
    /// every entry and breaks LG-4 without producing a single error. Applied through
    /// <c>PostConfigure</c> so it runs after configuration binding and cannot be turned off
    /// by an appsettings file.
    /// </description></item>
    /// <item><description>
    /// <b>Trace and span identifiers are tracked.</b> ASP.NET Core already starts an
    /// <c>Activity</c> per request; enabling activity tracking surfaces its identifiers in
    /// the log scope. This connects logs to traces for free and is the foundation
    /// NFR-OBS-004 builds on — it is not a tracing implementation, and no listener,
    /// sampler, or exporter is configured.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static void AddStructuredLogging(this IServiceCollection services)
    {
        services.AddLogging(static logging => logging.Configure(static options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId));

        services.PostConfigure<JsonConsoleFormatterOptions>(static o => o.IncludeScopes = true);
        services.PostConfigure<SimpleConsoleFormatterOptions>(static o => o.IncludeScopes = true);
    }

    /// <summary>
    /// Seam for metrics instrumentation (NFR-OBS-003, NFR-OBS-007).
    /// </summary>
    /// <remarks>
    /// Intentionally empty. Request rate, error rate, latency distribution, and saturation
    /// are registered by the milestone that introduces the subsystems being measured —
    /// instrumenting a host with no endpoints would measure nothing.
    /// </remarks>
    private static IServiceCollection AddMetricsInstrumentation(this IServiceCollection services) =>
        services;

    /// <summary>
    /// Seam for distributed tracing (NFR-OBS-004, NFR-OBS-006).
    /// </summary>
    /// <remarks>
    /// Intentionally empty. Tracing must cover the full request path <i>including provider
    /// calls</i>, so it arrives with the Gateway rather than before it. The correlation
    /// foundation in this milestone is what those traces will be keyed by.
    /// </remarks>
    private static IServiceCollection AddTracingInstrumentation(this IServiceCollection services) =>
        services;
}
