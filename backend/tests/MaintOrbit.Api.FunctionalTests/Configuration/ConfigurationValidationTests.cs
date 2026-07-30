using MaintOrbit.Api.Configuration;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Shared.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Configuration;

/// <summary>
/// Proves the configuration foundation fails fast.
/// </summary>
/// <remarks>
/// The value of startup validation is entirely in whether it actually fires. These tests
/// assert that a misconfigured deployment is rejected rather than starting and failing
/// later — including the security rule from specification §3.8, where a wildcard origin
/// combined with credentials must be refused.
/// </remarks>
public sealed class ConfigurationValidationTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddApplicationConfiguration(configuration);

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Application:Name"] = "MaintOrbit AI",
        ["Application:PublicBaseUrl"] = "https://api.example.test",
        ["Api:BasePath"] = "/api/v1",
        ["Api:DefaultPageSize"] = "50",
        ["Api:MaxPageSize"] = "200",
        ["Api:MaxQueryRangeDays"] = "90",
        ["Api:MaxFilterValuesPerParameter"] = "20",
        ["Cors:AllowCredentials"] = "true",
        ["Cors:AllowedOrigins:0"] = "https://console.example.test",
        ["HealthChecks:Enabled"] = "true",
        ["HealthChecks:LivenessPath"] = "/health/live",
        ["HealthChecks:ReadinessPath"] = "/health/ready"
    };

    [Fact]
    public void ValidConfiguration_BindsSuccessfully()
    {
        var provider = BuildProvider(ValidSettings());

        var api = provider.GetRequiredService<IOptions<ApiOptions>>().Value;
        var app = provider.GetRequiredService<IOptions<ApplicationOptions>>().Value;
        var cors = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var health = provider.GetRequiredService<IOptions<HealthCheckOptions>>().Value;

        Assert.Equal("/api/v1", api.BasePath);
        Assert.Equal(50, api.DefaultPageSize);
        Assert.Equal(200, api.MaxPageSize);
        Assert.Equal("MaintOrbit AI", app.Name);
        Assert.Single(cors.AllowedOrigins);
        Assert.NotEqual(health.LivenessPath, health.ReadinessPath);
    }

    [Fact]
    public void MissingRequiredValue_Fails()
    {
        var settings = ValidSettings();
        settings["Application:PublicBaseUrl"] = string.Empty;

        var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ApplicationOptions>>().Value);
    }

    [Fact]
    public void DefaultPageSizeAboveMaximum_Fails()
    {
        var settings = ValidSettings();
        settings["Api:DefaultPageSize"] = "500";

        var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ApiOptions>>().Value);
    }

    [Fact]
    public void WildcardOriginWithCredentials_Fails()
    {
        // Specification §3.8: never a wildcard with credentials.
        var settings = ValidSettings();
        settings["Cors:AllowedOrigins:0"] = "*";

        var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CorsOptions>>().Value);
    }

    [Fact]
    public void OriginWithTrailingSlash_Fails()
    {
        var settings = ValidSettings();
        settings["Cors:AllowedOrigins:0"] = "https://console.example.test/";

        var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CorsOptions>>().Value);
    }

    [Fact]
    public void IdenticalHealthPaths_Fail()
    {
        var settings = ValidSettings();
        settings["HealthChecks:ReadinessPath"] = "/health/live";

        var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<HealthCheckOptions>>().Value);
    }

    [Theory]
    [InlineData(EnvironmentNames.Development)]
    [InlineData(EnvironmentNames.Testing)]
    [InlineData(EnvironmentNames.Staging)]
    [InlineData(EnvironmentNames.Production)]
    public void RecognisedEnvironment_IsAccepted(string environmentName)
    {
        var environment = new StubHostEnvironment(environmentName);

        ConfigurationServiceCollectionExtensions.ValidateEnvironment(environment);
    }

    [Theory]
    [InlineData("Prod")]
    [InlineData("production")]
    [InlineData("")]
    public void UnrecognisedEnvironment_Fails(string environmentName)
    {
        var environment = new StubHostEnvironment(environmentName);

        Assert.Throws<InvalidOperationException>(
            () => ConfigurationServiceCollectionExtensions.ValidateEnvironment(environment));
    }

    /// <summary>
    /// Minimal <see cref="IHostEnvironment"/> for environment-name validation.
    /// </summary>
    /// <remarks>
    /// The framework's own implementation is internal. Only the environment name matters
    /// here, so a stub is clearer than reaching for reflection or an extra dependency.
    /// </remarks>
    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "MaintOrbit.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
