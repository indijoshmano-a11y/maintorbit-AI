namespace MaintOrbit.Application.Abstractions.Maintenance;

/// <summary>
/// Keeps <c>auditing.audit_events</c> supplied with partitions ahead of need.
/// </summary>
/// <remarks>
/// <b>Why this is a scheduled job rather than a migration.</b> §9.2 says partitions are "created
/// ahead of need by a scheduled job", and T-5 states the consequence of not having one: a missing
/// partition is an outage of the ingestion path. Because audit emission is fail-open (ADR-0021),
/// that outage does not present as a failed request — it presents as AU-8 incidents in the log and
/// audit events silently lost. A migration can only create a fixed window; the window then expires
/// on a date nobody is watching.
/// <para>
/// Declared in the application layer and implemented in infrastructure, because the operation is
/// PostgreSQL DDL and the layer that owns the database owns the statement (ADR-0001).
/// </para>
/// </remarks>
public interface IAuditPartitionMaintenance
{
    /// <summary>
    /// Runs one maintenance cycle.
    /// </summary>
    /// <remarks>
    /// Idempotent. Running it twice, concurrently or in sequence, must leave the same state and
    /// raise nothing — ADR-0014 makes idempotency mandatory for every job, and this one is
    /// scheduled, so it will run again while a previous run is still finishing at least once.
    /// <para>
    /// Never throws for an ordinary failure. The result says what happened; the caller decides
    /// what to log and what to surface as health. A cycle that threw would be a cycle that stopped
    /// the Worker, and the next cycle is this job's only retry.
    /// </para>
    /// </remarks>
    Task<AuditPartitionMaintenanceResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one maintenance cycle did, and what it found.</summary>
/// <param name="LockAcquired">
/// Whether this instance held the maintenance lock. <see langword="false"/> means another instance
/// was already working, which is a normal outcome rather than a failure.
/// </param>
/// <param name="PartitionsCreated">Partitions created during this cycle.</param>
/// <param name="PartitionsDropped">Partitions dropped during this cycle.</param>
/// <param name="RetentionEligible">
/// Partitions past the retention period. Populated whether or not dropping is enabled, so an
/// operator can see what *would* be removed before allowing it.
/// </param>
/// <param name="Unexpected">
/// Partitions whose name or bounds do not match the scheme. Reported, never repaired — §5 of the
/// milestone brief and ordinary caution agree here: a partition holding audit rows is evidence,
/// and automatic repair of something unrecognised is how evidence is destroyed by a well-meaning
/// job.
/// </param>
/// <param name="Failure">
/// The reason the cycle could not complete, or <see langword="null"/>. A cycle that failed to
/// create a partition is an operational failure, because the ingestion path depends on it.
/// </param>
public sealed record AuditPartitionMaintenanceResult(
    bool LockAcquired,
    IReadOnlyList<string> PartitionsCreated,
    IReadOnlyList<string> PartitionsDropped,
    IReadOnlyList<string> RetentionEligible,
    IReadOnlyList<string> Unexpected,
    string? Failure)
{
    /// <summary>A cycle that did no work because another instance held the lock.</summary>
    public static AuditPartitionMaintenanceResult NotHeld() =>
        new(LockAcquired: false, [], [], [], [], Failure: null);

    /// <summary>Whether the cycle completed without an operational failure.</summary>
    public bool Succeeded => Failure is null;
}
