using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers how the database context is registered.
/// </summary>
/// <remarks>
/// None of these open a connection. Resolving a context, building its model, and reading its
/// provider are all offline operations — which is why they can run in the ordinary test suite
/// rather than needing a database.
/// </remarks>
public sealed class DbContextRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] =
                    "Host=localhost;Database=maintorbit_test;Username=maintorbit"
            })
            .Build();

        var services = new ServiceCollection();
        services
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddApi(configuration)
            .AddObservability(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void DbContext_ResolvesFromAScope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>());
    }

    [Fact]
    public void DbContext_IsScoped_NotShared()
    {
        // A context carries change-tracking state for a unit of work. Sharing one across
        // requests would leak entities between them, which under multi-tenancy means leaking
        // them between Companies.
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<MaintOrbitDbContext>(),
            second.ServiceProvider.GetRequiredService<MaintOrbitDbContext>());
    }

    [Fact]
    public void Provider_IsPostgreSQL()
    {
        // ADR-0004 makes PostgreSQL the single system of record. Asserted because a provider
        // swapped for convenience — an in-memory provider in a test host, say — would silently
        // remove row-level security, which is the tenancy control itself.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }

    [Fact]
    public void Model_Builds()
    {
        // Accessing Model forces model creation, so the conventions in OnModelCreating run.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.NotNull(context.Model);
    }

    [Fact]
    public void Model_ExposesOnlyTheIdentityAggregatesBuiltSoFar()
    {
        // Updated deliberately in 11.1, which is what the previous "model is empty" assertion
        // existed to force. Every entity is named, so the next one added updates this too.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.Equal(
            ["MaintOrbit.Domain.Modules.Identity.Entities.Employee"],
            context.Model.GetEntityTypes().Select(entity => entity.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CommandTimeout_ComesFromConfiguration()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.Equal(30, context.Database.GetCommandTimeout());
    }
}
