using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Api.FunctionalTests.Middleware;

/// <summary>
/// An in-memory host composing the real pipeline around endpoints the tests control.
/// </summary>
/// <remarks>
/// The application deliberately exposes no endpoint that throws, so exception handling cannot
/// be exercised through the real host. This composes <see cref="PipelineExtensions.UseApiPipeline"/>
/// — the actual ordering under test, not a reimplementation of it — around endpoints that
/// produce the outcomes the middleware has to handle.
/// </remarks>
internal static class PipelineTestHost
{
    /// <summary>Path of an endpoint that always throws.</summary>
    public const string ThrowingPath = "/throws";

    /// <summary>Path of an endpoint that succeeds.</summary>
    public const string SucceedingPath = "/succeeds";

    /// <summary>Path of an endpoint that throws after the response has begun.</summary>
    public const string ThrowsAfterResponseStartedPath = "/throws-late";

    public static IHost Build(RecordingLoggerProvider? logRecorder = null) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
                        {
                            ["Application:Name"] = "MaintOrbit AI",
                            ["Application:PublicBaseUrl"] = "https://api.example.test",
                            ["Cors:AllowCredentials"] = "true",
                            ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                            ["Persistence:ConnectionString"] = "Host=localhost;Database=maintorbit_test;Username=maintorbit"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddRouting();
                    services
                        .AddApplication()
                        .AddInfrastructure(configuration)
                        .AddApi(configuration)
                        .AddObservability(configuration);

                    if (logRecorder is not null)
                    {
                        services.AddSingleton<ILoggerProvider>(logRecorder);
                    }
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Explicit lambda return types: a lambda whose body is a `throw`
                        // expression has no inferable return type, and overload resolution
                        // would otherwise bind to the RequestDelegate form.
                        endpoints.MapGet(SucceedingPath, static string () => "ok");

                        endpoints.MapGet(ThrowingPath, static string () =>
                            throw new InvalidOperationException(
                                "sensitive-internal-detail-must-not-reach-the-caller"));

                        endpoints.MapGet(ThrowsAfterResponseStartedPath, static async Task (HttpContext context) =>
                        {
                            await context.Response.WriteAsync("partial").ConfigureAwait(false);
                            await context.Response.Body.FlushAsync().ConfigureAwait(false);
                            throw new InvalidOperationException("too late to rewrite");
                        });
                    });
                }))
            .Build();
}
