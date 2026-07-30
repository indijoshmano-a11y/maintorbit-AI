using MaintOrbit.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Observability;

/// <summary>
/// Covers the logging settings that configuration is not permitted to override.
/// </summary>
/// <remarks>
/// Log levels and formatter choice are configuration, and deliberately so. Scope inclusion is
/// not: the correlation identifier travels in a logging scope, so a formatter with scopes
/// disabled drops it from every entry and breaks LG-4 without producing an error anywhere.
/// These tests assert the difference holds.
/// </remarks>
public sealed class LoggingConfigurationTests
{
    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopesRemainEnabled_WhateverConfigurationAsksFor(bool configuredValue)
    {
        // PostConfigure runs after every Configure callback, including the ones the host adds
        // when it binds Logging:Console:FormatterOptions. An appsettings file therefore cannot
        // turn scopes off, whichever way it is written.
        var services = new ServiceCollection();
        services.AddObservability(EmptyConfiguration());
        services.Configure<JsonConsoleFormatterOptions>(o => o.IncludeScopes = configuredValue);
        services.Configure<SimpleConsoleFormatterOptions>(o => o.IncludeScopes = configuredValue);

        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>()
            .Value.IncludeScopes);
        Assert.True(provider.GetRequiredService<IOptions<SimpleConsoleFormatterOptions>>()
            .Value.IncludeScopes);
    }

    [Fact]
    public void TraceAndSpanIdentifiers_AreTrackedIntoTheLogScope()
    {
        // ASP.NET Core already starts an Activity per request. Tracking its identifiers is
        // what lets a log entry be matched to a trace later — the foundation NFR-OBS-004
        // builds on, without registering any listener, sampler, or exporter.
        var services = new ServiceCollection();
        services.AddObservability(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value;

        Assert.True(options.ActivityTrackingOptions.HasFlag(ActivityTrackingOptions.TraceId));
        Assert.True(options.ActivityTrackingOptions.HasFlag(ActivityTrackingOptions.SpanId));
    }

    [Fact]
    public void LoggerFactory_IsResolvableWithoutTheApplicationTouchingIt()
    {
        // ILogger<T> is the injection point the code uses; ILoggerFactory exists for the host.
        // Asserting the typed logger resolves keeps the codebase off the factory.
        var services = new ServiceCollection();
        services.AddObservability(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ILogger<LoggingConfigurationTests>>());
    }
}
