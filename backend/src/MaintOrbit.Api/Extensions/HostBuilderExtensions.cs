namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Host-level configuration for the service provider.
/// </summary>
public static class HostBuilderExtensions
{
    /// <summary>
    /// Builds the service provider with scope and registration validation enabled.
    /// </summary>
    /// <remarks>
    /// The framework enables both checks in Development only. They are enabled in every
    /// environment here, deliberately.
    /// <para>
    /// <b>Scope validation</b> catches a captive dependency — a singleton holding a scoped
    /// service (DI-4). The captured instance outlives its scope and is then shared across
    /// every subsequent request. In a multi-tenant system that is the worst possible
    /// defect: a captured request-scoped tenant context would serve one Company's data to
    /// another, and it would present as correct behaviour rather than an error.
    /// </para>
    /// <para>
    /// <b>Build validation</b> resolves every registration at startup, so a missing
    /// dependency fails immediately instead of on the first request that happens to need
    /// it — possibly hours later, possibly in production. The cost is a small addition to
    /// startup time, paid once per process, against a failure that would otherwise reach a
    /// customer.
    /// </para>
    /// </remarks>
    public static IHostBuilder UseValidatedServiceProvider(this IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseDefaultServiceProvider(static (_, options) =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
    }
}
