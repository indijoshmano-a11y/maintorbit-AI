using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Proves the persistence validator is actually reached through the composition root.
/// </summary>
/// <remarks>
/// Testing a validator directly proves the rules are right; it does not prove they run.
/// Those are separate failures, and the second one is silent — an unregistered validator
/// leaves every one of its tests passing while nothing it checks is enforced. This suite
/// exercises the wiring rather than the rules.
/// </remarks>
public sealed class PersistenceValidationWiringTests
{
    private static IOptions<PersistenceOptions> ResolveOptions(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] = connectionString
            }))
            .Build();

        var services = new ServiceCollection();
        services
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddApi(configuration)
            .AddObservability(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<PersistenceOptions>>();
    }

    [Fact]
    public void ValidConfiguration_Resolves()
    {
        var options = ResolveOptions("Host=localhost;Database=maintorbit;Username=maintorbit");

        Assert.Equal("maintorbit", new Npgsql.NpgsqlConnectionStringBuilder(
            options.Value.ConnectionString).Database);
    }

    [Fact]
    public void CrossPropertyRules_AreEnforcedThroughTheCompositionRoot()
    {
        // Multiplexing is rejected by PersistenceOptionsValidator, not by DataAnnotations, so
        // this fails only if that validator is genuinely registered and consulted.
        var options = ResolveOptions("Host=localhost;Database=maintorbit;Username=u;Multiplexing=true");

        var failure = Assert.Throws<OptionsValidationException>(() => options.Value);

        Assert.Contains("Multiplexing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataAnnotationRules_AreEnforcedThroughTheCompositionRoot()
    {
        var options = ResolveOptions("");

        var failure = Assert.Throws<OptionsValidationException>(() => options.Value);

        Assert.Contains("ConnectionString", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothValidators_SurviveDuplicateRegistration()
    {
        // AddInfrastructure is called once by the composition root, but registering the
        // validator with AddSingleton rather than TryAddSingleton means a duplicate call adds
        // it twice. Validating twice is harmless; not validating at all is not.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
            {
                ["Persistence:ConnectionString"] = "Host=localhost;Database=m;Username=u;Multiplexing=true"
            }))
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        services.AddInfrastructure(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<PersistenceOptions>>();

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }
}
