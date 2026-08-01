using System.Net;
using MaintOrbit.Shared.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MaintOrbit.Api.FunctionalTests.Observability;

/// <summary>
/// Covers the health endpoints against the real host.
/// </summary>
/// <remarks>
/// These run through <see cref="WebApplicationFactory{TEntryPoint}"/> rather than against the
/// extension method in isolation, because what matters is that the endpoints are reachable at
/// the configured paths in the composed application. NFR-OBS-005 is an operational contract:
/// the orchestrator and load balancer call these URLs, and a check that passes in a unit test
/// while the route is unmapped would fail exactly where it is least visible.
/// </remarks>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentNames.Development);

            // The signing key has no default and is validated at startup, so the host cannot
            // start without one. Generated per assembly, never committed.
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(TestJwtConfiguration.Settings));
        });
    }

    [Fact]
    public async Task Liveness_ReportsHealthy()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_ReportsHealthy_WhenNoDependenciesAreRegistered()
    {
        // Nothing carries the Ready tag yet, so readiness reports healthy as soon as the host
        // is up. That is correct rather than provisional — an instance with no dependencies
        // genuinely is ready to serve.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LivenessAndReadiness_AreSeparateEndpoints()
    {
        // NFR-OBS-005 exists because the two answers have different consequences: failing
        // liveness restarts the container, failing readiness only removes it from rotation.
        // Serving one path from both would collapse that distinction without any visible
        // symptom until an outage.
        using var client = _factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task UnmappedPath_IsNotFound()
    {
        // Control. Establishes that the health responses above come from mapped endpoints
        // rather than from something answering every request with 200.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void CorrelationHeaderName_MatchesTheDocumentedContract()
    {
        // api-specification §4.2 names this header on every response, and the CORS allowlist
        // in appsettings.json permits it inbound. Both refer to the same literal, so it is
        // pinned here rather than left to agree by coincidence.
        Assert.Equal("X-Correlation-Id", CorrelationHeaderNames.CorrelationId);
    }
}
