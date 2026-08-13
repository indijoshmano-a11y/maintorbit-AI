namespace MaintOrbit.Worker;

/// <summary>
/// Readiness for a process that serves no traffic.
/// </summary>
/// <remarks>
/// <b>A background role has no request to fail, so health has to be reported some other way.</b>
/// The API answers a probe endpoint; the Worker cannot, because it has no listener and adding one
/// would make it a web host for the sake of a health check.
/// <para>
/// What a supervisor can observe is the process being alive and the structured log. This type
/// exists so "is the last cycle healthy?" is a single readable thing rather than a field on a
/// background service — and so the answer can be surfaced by whatever probe mechanism a later
/// milestone chooses, without changing the service.
/// </para>
/// <para>
/// It is deliberately not a <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> registration:
/// that package's value is the HTTP endpoint, and there is none here.
/// </para>
/// </remarks>
internal sealed class WorkerHealth
{
    private readonly AuditPartitionMaintenanceService _maintenance;

    public WorkerHealth(AuditPartitionMaintenanceService maintenance)
    {
        ArgumentNullException.ThrowIfNull(maintenance);

        _maintenance = maintenance;
    }

    /// <summary>
    /// Whether the most recent maintenance cycle completed.
    /// </summary>
    /// <remarks>
    /// False is worth acting on: audit emission is fail-open, so a Worker that has stopped
    /// creating partitions produces no failed requests and no customer complaint — only events
    /// that quietly stop being recorded once the horizon lapses.
    /// </remarks>
    public bool IsHealthy => _maintenance.LastCycleSucceeded;
}
