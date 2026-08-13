using System.Globalization;
using MaintOrbit.Application.Abstractions.Maintenance;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaintOrbit.Infrastructure.Maintenance;

/// <summary>
/// Creates <c>auditing.audit_events</c> partitions ahead of need and reports expired ones.
/// </summary>
/// <remarks>
/// <b>Opens its own connection rather than using the request <c>DbContext</c>.</b> Three reasons,
/// each sufficient: this is DDL and EF has no expression for it; the Worker has no request and
/// therefore no tenant, while the context's interceptor exists to set one; and an advisory lock is
/// held for a session, so the connection's lifetime has to be the operation's lifetime rather than
/// whatever the pool decides.
/// <para>
/// It touches no rows. Retention on an append-only relation is a partition drop and nothing else
/// (DB-P5) — there is no <c>DELETE</c> here, and adding one would defeat the guarantee the store
/// exists to make.
/// </para>
/// </remarks>
internal sealed partial class AuditPartitionMaintenance(
    IOptions<PersistenceOptions> persistence,
    IOptions<AuditPartitionOptions> options,
    ILogger<AuditPartitionMaintenance> logger,
    TimeProvider timeProvider)
    : IAuditPartitionMaintenance
{
    /// <summary>
    /// The advisory lock key for audit partition maintenance.
    /// </summary>
    /// <remarks>
    /// An arbitrary but fixed constant. PostgreSQL advisory locks live in one namespace across the
    /// database, so the value only has to be stable and not collide with another use — it is
    /// recorded here rather than computed from a hash so that an operator holding
    /// <c>pg_locks</c> open can identify it without running the application.
    /// </remarks>
    internal const long LockKey = 0x4D4F_4155_4449_5401; // "MOAUDIT" + 1

    public async Task<AuditPartitionMaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await using var connection = new NpgsqlConnection(persistence.Value.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await TryAcquireLockAsync(connection, settings, cancellationToken).ConfigureAwait(false))
            {
                // Another instance is doing the work. Not a failure — the outcome this cycle wanted
                // is being produced by somebody else.
                LockHeldElsewhere(logger);
                return AuditPartitionMaintenanceResult.NotHeld();
            }
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException)
        {
            // Could not even reach the database. The next cycle is this job's retry: ADR-0014
            // documents idempotency rather than a retry policy, and inventing an aggressive one
            // here would hammer a database that is already unwell.
            CycleFailed(logger, error);
            return new AuditPartitionMaintenanceResult(
                LockAcquired: false, [], [], [], [], $"Could not connect: {error.GetType().Name}.");
        }

        try
        {
            return await MaintainAsync(connection, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            CycleFailed(logger, error);

            return new AuditPartitionMaintenanceResult(
                LockAcquired: true, [], [], [], [], error.Message);
        }
        finally
        {
            // Advisory locks are released when the session ends, and the connection is closing
            // either way — but releasing explicitly means a pooled connection cannot carry the
            // lock back into the pool and block the next cycle for the process lifetime.
            await ReleaseLockAsync(connection).ConfigureAwait(false);
        }
    }

    private async Task<AuditPartitionMaintenanceResult> MaintainAsync(
        NpgsqlConnection connection,
        AuditPartitionOptions settings,
        CancellationToken cancellationToken)
    {
        var (existing, unexpected) = await ReadPartitionsAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        foreach (var name in unexpected)
        {
            // Reported, never touched. A partition holding audit rows is evidence; a job that
            // "repaired" something it did not recognise is how evidence is destroyed by accident.
            UnexpectedPartition(logger, name);
        }

        var now = timeProvider.GetUtcNow();
        var created = await CreateMissingAsync(connection, existing, settings, now, cancellationToken)
            .ConfigureAwait(false);

        var eligible = existing
            .Where(partition => partition.IsExpired(now, settings.RetentionMonths))
            .OrderBy(partition => partition.From)
            .Select(partition => partition.Name)
            .ToList();

        var dropped = new List<string>();

        if (settings.DropExpiredPartitions)
        {
            dropped = await DropAsync(connection, eligible, cancellationToken).ConfigureAwait(false);
        }
        else if (eligible.Count > 0)
        {
            // Visible rather than silent. An operator who has confirmed no legal hold applies can
            // then enable dropping deliberately; until then the storage cost is the safer error.
            //
            // Joined into a local rather than passed as an expression: the cycle runs daily over a
            // list bounded by the retention window, so the allocation is irrelevant, and a local is
            // not something the caller has to reason about being evaluated eagerly.
            var names = string.Join(", ", eligible);

            RetentionEligibleButDisabled(logger, eligible.Count, names);
        }

        return new AuditPartitionMaintenanceResult(
            LockAcquired: true, created, dropped, eligible, unexpected, Failure: null);
    }

    /// <summary>
    /// Reads every partition of <c>auditing.audit_events</c> from the catalogue.
    /// </summary>
    private static async Task<(IReadOnlyList<AuditPartition> Known, IReadOnlyList<string> Unexpected)>
        ReadPartitionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT c.relname, pg_get_expr(c.relpartbound, c.oid)
            FROM pg_class c
            JOIN pg_inherits i ON i.inhrelid = c.oid
            WHERE i.inhparent = 'auditing.audit_events'::regclass
            ORDER BY c.relname;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var known = new List<AuditPartition>();
        var unexpected = new List<string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var bound = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            var partition = AuditPartition.TryRead(name, bound);

            if (partition is null)
            {
                unexpected.Add(name);
            }
            else
            {
                known.Add(partition);
            }
        }

        return (known, unexpected);
    }

    /// <summary>
    /// Creates every month from the current one to the horizon that does not already exist.
    /// </summary>
    /// <remarks>
    /// Starts at the current month rather than the next, so a database restored from a backup — or
    /// one where the current partition was dropped by hand — is repaired rather than left to fail
    /// on the next insert.
    /// <para>
    /// <c>CREATE TABLE ... IF NOT EXISTS</c> is not used, deliberately. The check is against the
    /// catalogue read above, so a partition that exists with the <i>wrong</i> bounds is reported as
    /// unexpected instead of being quietly skipped by a statement that only looks at the name.
    /// </remarks>
    private async Task<IReadOnlyList<string>> CreateMissingAsync(
        NpgsqlConnection connection,
        IReadOnlyList<AuditPartition> existing,
        AuditPartitionOptions settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var present = existing.Select(partition => partition.Name).ToHashSet(StringComparer.Ordinal);
        var created = new List<string>();
        var month = AuditPartition.MonthStart(now);

        for (var offset = 0; offset <= settings.FutureMonths; offset++)
        {
            var start = month.AddMonths(offset);
            var name = AuditPartition.NameFor(start);

            if (present.Contains(name))
            {
                continue;
            }

            await CreatePartitionAsync(connection, name, start, start.AddMonths(1), cancellationToken)
                .ConfigureAwait(false);

            created.Add(name);
            PartitionCreated(logger, name);
        }

        return created;
    }

    /// <summary>
    /// Creates one partition, with the security properties the parent's do not confer.
    /// </summary>
    /// <remarks>
    /// <b>Row-level security is not inherited, and this is the whole reason partition creation
    /// cannot be left to a bare <c>CREATE TABLE ... PARTITION OF</c>.</b> A partition is a table:
    /// policies on the parent apply when rows are reached through the parent, but a partition
    /// addressed directly answers to its own. A new partition without them would be a relation
    /// holding every Company's audit events with no isolation and no append-only protection —
    /// and nothing would fail, because the application reaches rows through the parent.
    /// <para>
    /// The statements match the 12.2 migration exactly. Two places now create partitions, so they
    /// must agree; <c>AuditPartitionMaintenanceTests</c> asserts a newly created partition carries
    /// the same protections as a migrated one.
    /// </para>
    /// </remarks>
    private static async Task CreatePartitionAsync(
        NpgsqlConnection connection,
        string name,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // DDL takes no parameters — neither the identifier nor the range bounds can be bound, and
        // a DO block would not help because its body is a string literal that never sees them. So
        // the statement is assembled, and the two inputs are made safe rather than trusted:
        //
        //   * the name is generated from a formatted UTC month, so it is `audit_events_YYYY_MM` and
        //     nothing else, and it is quoted anyway rather than relying on that staying true;
        //   * the bounds are rendered as ISO-8601 UTC, a format with no quoting significance.
        //
        // Everything runs in one transaction. Without it, a process killed between CREATE TABLE and
        // the statements below would leave a partition holding every Company's audit events with no
        // row-level security and no append-only protection — and nothing would notice, because the
        // application reaches rows through the parent.
        var identifier = $"auditing.{QuoteIdentifier(name)}";
        var lower = from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var upper = to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var statements = new[]
        {
            $"CREATE TABLE {identifier} PARTITION OF auditing.audit_events " +
            $"FOR VALUES FROM ('{lower}') TO ('{upper}');",

            // Row-level security is not inherited from the parent. These four statements are what
            // make a created partition as safe as a migrated one.
            $"ALTER TABLE {identifier} ENABLE ROW LEVEL SECURITY;",
            $"ALTER TABLE {identifier} FORCE ROW LEVEL SECURITY;",

            $"""
             CREATE POLICY rls_audit_events_read ON {identifier}
                 FOR SELECT
                 USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
             """,

            $"""
             CREATE POLICY rls_audit_events_append ON {identifier}
                 FOR INSERT
                 WITH CHECK (company_id IS NOT DISTINCT FROM NULLIF(current_setting('app.current_company_id', true), '')::uuid);
             """,

            $"REVOKE UPDATE, DELETE ON {identifier} FROM CURRENT_USER;"
        };

        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops expired partitions, one statement each.
    /// </summary>
    /// <remarks>
    /// One at a time and logged individually at warning level, because this is the only operation
    /// in the system that destroys audit history. A batch that failed halfway would leave an
    /// operator guessing which months survived.
    /// </remarks>
    private async Task<List<string>> DropAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> eligible,
        CancellationToken cancellationToken)
    {
        var dropped = new List<string>();

        foreach (var name in eligible)
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    $"DROP TABLE auditing.{QuoteIdentifier(name)};", connection);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                dropped.Add(name);
                PartitionDropped(logger, name);
            }
            catch (NpgsqlException error)
            {
                // One partition failing to drop must not stop the others, and must not stop the
                // Worker. Storage is the cost of continuing; a stalled maintenance loop costs
                // partition creation, which is the half that loses data.
                DropFailed(logger, name, error);
            }
        }

        return dropped;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<bool> TryAcquireLockAsync(
        NpgsqlConnection connection,
        AuditPartitionOptions settings,
        CancellationToken cancellationToken)
    {
        // A bounded wait, then give up. pg_try_advisory_lock returns immediately; the statement
        // timeout bounds the round trip rather than the wait, which is what "bounded" needs to mean
        // for a lock that is never queued for.
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key);", connection)
        {
            CommandTimeout = settings.LockTimeoutSeconds
        };

        command.Parameters.AddWithValue("key", LockKey);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", connection);
            command.Parameters.AddWithValue("key", LockKey);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The session is going away regardless, which releases the lock. Failing to unlock
            // explicitly is not worth turning a completed cycle into a reported failure.
        }
    }

    [LoggerMessage(
        EventId = 1700,
        Level = LogLevel.Information,
        Message = "Created audit partition {Partition}.")]
    private static partial void PartitionCreated(ILogger logger, string partition);

    [LoggerMessage(
        EventId = 1701,
        Level = LogLevel.Warning,
        Message = "Dropped audit partition {Partition}. Audit history in this range is gone.")]
    private static partial void PartitionDropped(ILogger logger, string partition);

    [LoggerMessage(
        EventId = 1702,
        Level = LogLevel.Error,
        Message = "Audit partition maintenance cycle failed. Audit emission is fail-open, so a " +
                  "missing partition loses events rather than failing a request. The next cycle " +
                  "retries.")]
    private static partial void CycleFailed(ILogger logger, Exception error);

    [LoggerMessage(
        EventId = 1703,
        Level = LogLevel.Warning,
        Message = "Audit partition {Partition} does not match the expected name or bounds. It has " +
                  "been left untouched and needs an operator.")]
    private static partial void UnexpectedPartition(ILogger logger, string partition);

    [LoggerMessage(
        EventId = 1704,
        Level = LogLevel.Information,
        Message = "{Count} audit partitions are past retention but dropping is disabled: " +
                  "{Partitions}. Enabling it requires confirming no legal hold applies (I-11).")]
    private static partial void RetentionEligibleButDisabled(
        ILogger logger, int count, string partitions);

    [LoggerMessage(
        EventId = 1705,
        Level = LogLevel.Error,
        Message = "Could not drop audit partition {Partition}. Maintenance continues.")]
    private static partial void DropFailed(ILogger logger, string partition, Exception error);

    [LoggerMessage(
        EventId = 1706,
        Level = LogLevel.Debug,
        Message = "Another instance holds the audit partition maintenance lock. Skipping this cycle.")]
    private static partial void LockHeldElsewhere(ILogger logger);
}
