using System.Net;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Shared.Constants;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Central registration for every strongly typed configuration section.
/// </summary>
/// <remarks>
/// One place binds configuration. <c>docs/08-development/coding-standards.md</c> CF-2
/// requires strongly typed options validated at startup, and the configuration rules for
/// this milestone require no duplicated binding logic and no magic strings — hence the
/// single <see cref="Register{TOptions,TValidator}"/> helper and the <c>SectionName</c>
/// constant on each options type.
/// </remarks>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Binds, validates, and registers all application configuration.
    /// </summary>
    /// <remarks>
    /// Every section is validated at startup rather than on first use. A misconfigured
    /// deployment fails immediately and visibly, instead of succeeding until the first
    /// request that happens to touch the bad value — possibly hours later, possibly in
    /// production.
    /// </remarks>
    public static IServiceCollection AddApplicationConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ApplicationOptions>()
            .Bind(configuration.GetSection(ApplicationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        Register<ApiOptions, ApiOptionsValidator>(services, configuration, ApiOptions.SectionName);
        Register<CorsOptions, CorsOptionsValidator>(services, configuration, CorsOptions.SectionName);
        Register<HealthCheckOptions, HealthCheckOptionsValidator>(services, configuration, HealthCheckOptions.SectionName);
        Register<ReverseProxyOptions, ReverseProxyOptionsValidator>(services, configuration, ReverseProxyOptions.SectionName);

        AddForwardedHeaders(services);

        return services;
    }

    /// <summary>
    /// Translates <see cref="ReverseProxyOptions"/> into the framework's forwarded headers
    /// settings.
    /// </summary>
    /// <remarks>
    /// Only <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are processed. <c>
    /// X-Forwarded-Host</c> is deliberately excluded: it rewrites the host the application
    /// believes it is serving, and the one place that would matter — generating absolute URLs
    /// a customer will follow — already reads <see cref="ApplicationOptions.PublicBaseUrl"/>
    /// for exactly that reason.
    /// <para>
    /// The framework's default trust list (loopback) is cleared and replaced. Leaving the
    /// defaults in place alongside a configured proxy would silently widen trust beyond what
    /// the configuration names.
    /// </para>
    /// </remarks>
    private static void AddForwardedHeaders(IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ReverseProxyOptions>>(static (forwarded, reverseProxy) =>
            {
                var options = reverseProxy.Value;

                forwarded.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = options.ForwardLimit;

                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();

                foreach (var proxy in options.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in options.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });
    }

    /// <summary>
    /// Validates that the running environment is one this application recognises.
    /// </summary>
    /// <remarks>
    /// An unrecognised <c>ASPNETCORE_ENVIRONMENT</c> — a typo such as "Prod" or
    /// "production" — silently behaves as a non-Development environment. That is the
    /// worst possible failure mode: development conveniences switch off, production
    /// hardening never switches on, and nothing reports a problem.
    /// <para>
    /// Called from the composition root before the host is built.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The environment name is not recognised.</exception>
    public static void ValidateEnvironment(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var isKnown = EnvironmentNames.All.Contains(environment.EnvironmentName, StringComparer.Ordinal);

        if (!isKnown)
        {
            throw new InvalidOperationException(
                $"Unrecognised environment '{environment.EnvironmentName}'. " +
                $"Expected one of: {string.Join(", ", EnvironmentNames.All)}. " +
                "Environment names are case-sensitive. See MaintOrbit.Shared.Constants.EnvironmentNames.");
        }
    }

    /// <summary>
    /// Binds one section, applies DataAnnotations and its cross-property validator,
    /// and defers failure to startup.
    /// </summary>
    private static void Register<TOptions, TValidator>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
    }
}
