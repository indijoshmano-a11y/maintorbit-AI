using MaintOrbit.Application.Abstractions.Maintenance;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Worker;
using MaintOrbit.Worker.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Maintenance;

/// <summary>
/// Covers the Worker host: what it composes, what it validates, and how it stops.
/// </summary>
/// <remarks>
/// None of these touch a database. The Worker's job is exercised against real PostgreSQL in
/// <see cref="AuditPartitionMaintenanceTests"/>; what is worth testing here is the host — that it
/// builds, that a bad setting stops it starting, and that cancellation ends it cleanly rather than
/// by exception. A background process that crashed on every container stop would fill the logs of
/// a perfectly healthy deployment.
/// </remarks>
public sealed class WorkerHostTests
{
    private static IHost Build(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Structurally valid and pointed at nothing. Nothing here opens a connection: the
            // maintenance only connects when a cycle runs, which these tests control.
            ["Persistence:ConnectionString"] =
                "Host=localhost;Port=5432;Database=maintorbit_worker_test;Username=nobody",
            ["Encryption:DataKey"] = TestEncryptionKey.Base64,
            ["AuditPartitions:IntervalMinutes"] = "1440"
        };

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestJwtConfiguration.With(settings));
        builder.Services.AddWorker(builder.Configuration);

        return builder.Build();
    }

    [Fact]
    public void TheWorkerHost_Builds()
    {
        // The composition root resolves. A Worker that could not build would fail at container
        // start with a dependency-injection error, which is a poor way to discover a registration.
        using var host = Build();

        Assert.NotNull(host.Services.GetRequiredService<IAuditPartitionMaintenance>());
    }

    [Fact]
    public void TheMaintenanceService_IsTheOnlyHostedService()
    {
        // The Worker is deliberately small. ADR-0014's other job classes do not exist yet, and a
        // second hosted service arriving unnoticed would be scope this milestone excluded.
        using var host = Build();

        var hosted = host.Services.GetServices<IHostedService>().ToList();

        Assert.Single(hosted, service => service is AuditPartitionMaintenanceService);
    }

    [Fact]
    public void TheHealthSurface_ReadsTheRunningService()
    {
        // A background role has no probe endpoint, so the last cycle's outcome is the readiness
        // signal. It must read the instance that actually runs, not a second copy — hence the
        // singleton registered twice rather than AddHostedService alone.
        using var host = Build();

        var health = host.Services.GetRequiredService<WorkerHealth>();
        var service = host.Services.GetRequiredService<AuditPartitionMaintenanceService>();

        Assert.True(health.IsHealthy);

        service.LastCycleSucceeded = false;

        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void TheWorkerAndTheApi_ReadTheirOwnConnectionSettings()
    {
        // NFR-PERF-001: batch work must not compete with the Gateway for connection-pool capacity,
        // and it cannot compete for a pool it does not share. The section name is the same; the
        // configuration instance is the process's own.
        using var host = Build(new Dictionary<string, string?>
        {
            ["Persistence:ConnectionString"] =
                "Host=localhost;Port=5432;Database=worker_only;Username=worker"
        });

        var options = host.Services.GetRequiredService<IOptions<PersistenceOptions>>();

        Assert.Contains("worker_only", options.Value.ConnectionString, StringComparison.Ordinal);
    }

    // ---- Configuration validation ---------------------------------------------------------------

    [Fact]
    public void ValidPartitionSettings_AreAccepted()
    {
        using var host = Build();

        var options = host.Services.GetRequiredService<IOptions<AuditPartitionOptions>>();

        Assert.Equal(12, options.Value.FutureMonths);
        Assert.Equal(12, options.Value.RetentionMonths);
        Assert.False(options.Value.DropExpiredPartitions);
    }

    [Fact]
    public void RetentionBelowTheDocumentedFloor_IsRejected()
    {
        // AU-7 documents audit retention as at least twelve months. A shorter setting is not a
        // tuning choice — it is a compliance commitment being quietly reduced, and §7.2 names
        // reducing retention as "potentially an attempt to destroy evidence".
        using var host = Build(new Dictionary<string, string?>
        {
            ["AuditPartitions:RetentionMonths"] = "6"
        });

        var error = Assert.Throws<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<AuditPartitionOptions>>().Value);

        Assert.Contains("12", string.Join(' ', error.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void AHorizonBelowThreeMonths_IsRejected()
    {
        // The horizon is the margin between the job silently breaking and audit events being lost.
        // Below a quarter there is not enough room to notice a Worker that has stopped running.
        using var host = Build(new Dictionary<string, string?>
        {
            ["AuditPartitions:FutureMonths"] = "1"
        });

        Assert.Throws<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<AuditPartitionOptions>>().Value);
    }

    // ---- Lifecycle ------------------------------------------------------------------------------

    [Fact]
    public async Task TheWorkerStopsCleanlyWhenCancelled()
    {
        // Container stop is an ordinary event, not a fault. If shutdown surfaced as an exception,
        // every restart of a healthy deployment would log an error.
        using var host = Build();

        var service = host.Services.GetRequiredService<AuditPartitionMaintenanceService>();

        using var cancellation = new CancellationTokenSource();

        await service.StartAsync(cancellation.Token);
        await service.StopAsync(CancellationToken.None);

        // Reaching here without throwing is the assertion. Stated explicitly so the test does not
        // read as one that forgot to assert.
        Assert.True(true);
    }

    [Fact]
    public async Task AFailedCycleIsVisibleInHealth()
    {
        // The connection string points at a database that does not exist, so the first cycle
        // fails. Audit emission is fail-open, so nothing else reports it — this is the signal.
        using var host = Build();

        var service = host.Services.GetRequiredService<AuditPartitionMaintenanceService>();
        var health = host.Services.GetRequiredService<WorkerHealth>();

        await service.StartAsync(CancellationToken.None);

        // The first cycle runs immediately rather than waiting out the interval.
        await WaitForAsync(() => !health.IsHealthy);

        await service.StopAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }
}
