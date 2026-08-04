using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers the tooling entry point used by <c>dotnet ef</c>.
/// </summary>
/// <remarks>
/// The factory is only ever invoked by the CLI, so nothing in the application would fail if it
/// broke. It would be discovered by a developer trying to add a migration — the moment least
/// convenient to find out.
/// </remarks>
public sealed class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void Factory_BuildsAContext_WithoutAnyEnvironmentConfiguration()
    {
        // Migration generation must work offline, with no environment variable set and no
        // database reachable. The placeholder connection string exists for exactly this.
        var factory = new DesignTimeDbContextFactory();

        using var context = factory.CreateDbContext([]);

        Assert.NotNull(context);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }

    [Fact]
    public void Factory_BuildsTheSameModelAsTheApplication()
    {
        // The reason both paths share NpgsqlConfiguration. If they configured the provider
        // separately they would drift, and the symptom is a migration generated against
        // settings the application does not run with — found only when it is applied.
        // The identity model must therefore be present here exactly as it is at runtime.
        var factory = new DesignTimeDbContextFactory();

        using var context = factory.CreateDbContext([]);

        Assert.Equal(14, context.Model.GetEntityTypes().Count());
    }

    [Fact]
    public void Factory_PrefersTheEnvironmentConnectionString()
    {
        var original = Environment.GetEnvironmentVariable(
            DesignTimeDbContextFactory.ConnectionStringVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                DesignTimeDbContextFactory.ConnectionStringVariable,
                "Host=db.example.test;Database=maintorbit_ci;Username=ci");

            var factory = new DesignTimeDbContextFactory();
            using var context = factory.CreateDbContext([]);

            Assert.Contains(
                "db.example.test",
                context.Database.GetConnectionString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DesignTimeDbContextFactory.ConnectionStringVariable, original);
        }
    }
}
