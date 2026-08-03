using System.Net;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Foundation;

/// <summary>
/// The Backend Foundation gate: everything milestones 10.1–10.6 built, exercised through the
/// real host in one place.
/// </summary>
/// <remarks>
/// Each subsystem already has its own focused tests. This suite exists for a different reason —
/// those tests compose what they need, and a subsystem can pass in isolation while the assembled
/// application does not start. Milestone 10.6 produced exactly that: a validator whose own tests
/// all passed while its registration was silently a no-op, caught only by starting the host.
/// <para>
/// So every assertion here runs against <see cref="WebApplicationFactory{TEntryPoint}"/> driving
/// the actual <c>Program</c>, not a hand-built service collection.
/// </para>
/// </remarks>
public sealed class BackendFoundationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BackendFoundationTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(static builder =>
        {
            builder.UseEnvironment(EnvironmentNames.Development);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(TestJwtConfiguration.Settings));
        });
    }

    // ---- Gate 1: the application starts -----------------------------------------------------

    [Fact]
    public void Application_Starts()
    {
        // Creating the client builds the host, which runs configuration validation, the options
        // ValidateOnStart hooks, and ValidateOnBuild across every registration.
        using var client = _factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void Host_BuildsWithScopeValidationEnabled()
    {
        // ValidateScopes is enabled in every environment, not just Development. Observable
        // because the root provider must refuse to hand out a scoped service — if this ever
        // stopped throwing, a captive dependency could reach production undetected, and under
        // multi-tenancy that means one Company's context serving another.
        var failure = Assert.Throws<InvalidOperationException>(
            () => _factory.Services.GetRequiredService<MaintOrbitDbContext>());

        Assert.Contains("scoped service", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Gate 2: configuration ---------------------------------------------------------------

    [Theory]
    [InlineData(typeof(IOptions<Api.Configuration.ApplicationOptions>))]
    [InlineData(typeof(IOptions<Api.Configuration.ApiOptions>))]
    [InlineData(typeof(IOptions<Api.Configuration.CorsOptions>))]
    [InlineData(typeof(IOptions<Api.Configuration.HealthCheckOptions>))]
    [InlineData(typeof(IOptions<Api.Configuration.ReverseProxyOptions>))]
    [InlineData(typeof(IOptions<PersistenceOptions>))]
    public void EveryOptionsSection_BindsAndValidates(Type optionsAccessor)
    {
        using var scope = _factory.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService(optionsAccessor);
        var value = optionsAccessor.GetProperty("Value")!.GetValue(accessor);

        Assert.NotNull(value);
    }

    [Fact]
    public void UnknownEnvironment_FailsStartup()
    {
        // An unrecognised ASPNETCORE_ENVIRONMENT silently behaves as non-Development:
        // development conveniences switch off, production hardening never switches on, and
        // nothing reports a problem.
        using var misconfigured = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(static builder =>
            {
                builder.UseEnvironment("Prod");
                builder.ConfigureAppConfiguration(configuration =>
                    configuration.AddInMemoryCollection(TestJwtConfiguration.Settings));
            });

        var failure = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());

        Assert.Contains("Prod", Flatten(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredSetting_FailsStartup()
    {
        // Persistence:ConnectionString is required. Nothing opens a connection at startup, so
        // without ValidateOnStart this would surface on the first request that touches the
        // database — to a customer rather than to an operator.
        using var misconfigured = Reconfigured(("Persistence:ConnectionString", ""));

        var failure = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());

        Assert.Contains("ConnectionString", Flatten(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSecuritySetting_FailsStartup()
    {
        // A wildcard origin with credentials is rejected by the browser at runtime and would
        // present as an unexplained CORS failure. api-specification §3.8 forbids the pairing, so
        // it fails here instead.
        using var misconfigured = Reconfigured(
            ("Cors:AllowedOrigins:0", "*"),
            ("Cors:AllowCredentials", "true"));

        Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());
    }

    // ---- Gate 3: dependency injection ---------------------------------------------------------

    [Fact]
    public void CrossCuttingServices_ResolveFromAScope()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICorrelationIdAccessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>());
    }

    // ---- Gate 4: the request pipeline ---------------------------------------------------------

    [Fact]
    public async Task HealthEndpoints_Respond()
    {
        using var client = _factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task MiddlewareComposes_AndReturnsCorrelationOnEveryResponse()
    {
        // Covers ordering as much as presence: the header is written by middleware registered
        // before routing, so a 404 produced by routing carries it too.
        using var client = _factory.CreateClient();

        using var found = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var missing = await client.GetAsync(new Uri("/not-mapped", UriKind.Relative));

        Assert.True(found.Headers.Contains(CorrelationHeaderNames.CorrelationId));
        Assert.True(missing.Headers.Contains(CorrelationHeaderNames.CorrelationId));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SuppliedCorrelationIdentifier_SurvivesTheWholePipeline()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeaderNames.CorrelationId, "foundation-gate-1");

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal("foundation-gate-1", Assert.Single(
            response.Headers.GetValues(CorrelationHeaderNames.CorrelationId)));
    }

    // ---- Gate 5: persistence -------------------------------------------------------------------

    [Fact]
    public void Persistence_UsesPostgreSQL_AndCarriesTheIdentityModel()
    {
        // ADR-0004 fixes PostgreSQL as the system of record; a provider swapped for test
        // convenience would silently remove row-level security, which is the tenancy control
        // itself.
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        Assert.Equal(8, context.Model.GetEntityTypes().Count());
    }

    [Fact]
    public void DesignTimeFactory_BuildsTheSameContextOffline()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }

    private static WebApplicationFactory<Program> Reconfigured(params (string Key, string Value)[] overrides)
    {
        var settings = overrides.ToDictionary(
            static o => o.Key, static o => (string?)o.Value, StringComparer.Ordinal);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentNames.Development);
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(TestJwtConfiguration.Settings);
                configuration.AddInMemoryCollection(settings);
            });
        });
    }

    private static string Flatten(Exception exception)
    {
        var text = exception.ToString();

        return exception is AggregateException aggregate
            ? string.Join(' ', [text, .. aggregate.InnerExceptions.Select(static e => e.ToString())])
            : text;
    }
}
