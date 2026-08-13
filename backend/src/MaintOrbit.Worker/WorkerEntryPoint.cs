using MaintOrbit.Worker.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Worker;

/// <summary>
/// The Worker process entry point (DP-001 — its own container, process, and connection pool).
/// </summary>
/// <remarks>
/// <b>A named class rather than top-level statements, and that is not style.</b> Top-level
/// statements generate a type called <c>Program</c>, and the API host already has one it exposes to
/// the test assembly for <c>WebApplicationFactory&lt;Program&gt;</c>. A test project referencing
/// both hosts would then see two <c>Program</c> types and fail to compile — so the Worker gives its
/// entry point a name of its own instead.
/// <para>
/// Deliberately thin. Everything it composes lives in
/// <see cref="WorkerServiceCollectionExtensions"/>, so the tests build the same graph without
/// running this, and this file stays a statement of what the process <i>is</i> rather than of what
/// it contains.
/// </para>
/// </remarks>
internal static class WorkerEntryPoint
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Machine-readable wherever logs are collected (NFR-OBS-001). Scopes stay on: losing them
        // would drop the correlation identifier from every entry, and a background process has no
        // request to recover it from.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        builder.Services.AddWorker(builder.Configuration);

        var host = builder.Build();

        // Options are validated on start, so a Worker configured with a retention period below the
        // documented floor, or a horizon reaching past retention, refuses to start rather than
        // failing on its first cycle — which, for a job whose failure mode is silently lost audit
        // events, would be discovered far too late.
        //
        // RunAsync installs the SIGTERM handler, so a container stop cancels the maintenance loop
        // and the host waits for it to unwind before exiting.
        await host.RunAsync().ConfigureAwait(false);
    }
}
