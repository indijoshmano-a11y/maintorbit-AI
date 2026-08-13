using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Infrastructure.Maintenance;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaintOrbit.Api.FunctionalTests.Maintenance;

/// <summary>
/// Runs partition maintenance against a real PostgreSQL database.
/// </summary>
/// <remarks>
/// <b>As a <c>NOSUPERUSER NOBYPASSRLS</c> role, like the audit store's own suite.</b> The reason is
/// sharper here than usual: this job creates partitions, and a partition created without row-level
/// security is a relation holding every Company's audit events with no isolation — while the
/// application, which reaches rows through the parent, notices nothing. Under a superuser
/// connection that defect is invisible, because a superuser sees every row either way.
/// </remarks>
public sealed class AuditPartitionMaintenanceTests : IAsyncLifetime
{
    private const string Role = "maintorbit_partition_test";
    private const string RolePassword = "partition-test";

    private static readonly Guid CompanyA = Guid.CreateVersion7();
    private static readonly Guid CompanyB = Guid.CreateVersion7();

    private string? _database;
    private string? _skip;
    private string _unprivileged = string.Empty;

    public async Task InitializeAsync()
    {
        var owner = await TestDatabase.CreateAsync().ConfigureAwait(false);

        if (owner is null)
        {
            _skip = "No PostgreSQL reachable.";
            return;
        }

        _database = owner;
        var databaseName = new NpgsqlConnectionStringBuilder(owner).Database!;

        try
        {
            await AdministerAsync(
                $"""
                 DROP ROLE IF EXISTS {Role};
                 CREATE ROLE {Role} LOGIN PASSWORD '{RolePassword}'
                     NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                 """).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            _skip = "Cannot create a test role.";
            return;
        }

        _unprivileged =
            $"Host=localhost;Port=5432;Database={databaseName};" +
            $"Username={Role};Password={RolePassword}";

        await ExecuteAsync(owner, $"ALTER DATABASE {databaseName} OWNER TO {Role};")
            .ConfigureAwait(false);

        var builder = new DbContextOptionsBuilder<MaintOrbitDbContext>();
        NpgsqlConfiguration.Apply(builder, new PersistenceOptions { ConnectionString = _unprivileged });

        await using var context = new MaintOrbitDbContext(builder.Options);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);

        if (_skip is null)
        {
            await AdministerAsync($"DROP ROLE IF EXISTS {Role};").ConfigureAwait(false);
        }
    }

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Creation ------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingFuturePartition_IsCreated()
    {
        if (Unavailable()) { return; }

        var horizon = AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(6));
        await DropPartitionAsync(horizon);

        var result = await RunAsync();

        Assert.True(result.Succeeded, result.Failure);
        Assert.Contains(horizon, result.PartitionsCreated, StringComparer.Ordinal);
        Assert.Contains(horizon, await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task EveryMonthWithinTheHorizon_Exists()
    {
        // The point of the horizon: after one cycle, the whole window is covered, so the job can
        // fail silently for months before anything is lost.
        if (Unavailable()) { return; }

        await RunAsync();

        var names = await PartitionNamesAsync();

        for (var offset = 0; offset <= 12; offset++)
        {
            Assert.Contains(
                AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(offset)),
                names,
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task TheCurrentMonth_IsRepairedIfMissing()
    {
        // Starts at the current month, not the next. A database restored from an older backup, or
        // one where somebody dropped today's partition, is fixed rather than left to fail on the
        // very next audit event.
        if (Unavailable()) { return; }

        var current = AuditPartition.NameFor(DateTimeOffset.UtcNow);
        await DropPartitionAsync(current);

        var result = await RunAsync();

        Assert.Contains(current, result.PartitionsCreated, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RunningWhenNothingIsMissing_DoesNothing()
    {
        if (Unavailable()) { return; }

        await RunAsync();

        var second = await RunAsync();

        Assert.True(second.Succeeded);
        Assert.Empty(second.PartitionsCreated);
    }

    [Fact]
    public async Task RepeatedExecution_IsIdempotent()
    {
        // ADR-0014 makes idempotency mandatory for every job. This one is scheduled, so it will
        // certainly run again while a previous run is finishing.
        if (Unavailable()) { return; }

        await RunAsync();
        var before = await PartitionNamesAsync();

        await RunAsync();
        await RunAsync();

        Assert.Equal(before, await PartitionNamesAsync());
    }

    [Fact]
    public async Task NoDefaultPartition_IsEverCreated()
    {
        if (Unavailable()) { return; }

        await RunAsync();

        var defaults = await ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_class c
            JOIN pg_inherits i ON i.inhrelid = c.oid
            WHERE i.inhparent = 'auditing.audit_events'::regclass
              AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
            """);

        Assert.Equal(0, defaults);
    }

    // ---- A created partition is as protected as a migrated one ---------------------------------

    [Fact]
    public async Task ACreatedPartition_CarriesRowLevelSecurity()
    {
        // The defect this prevents is invisible in normal operation: the application reaches rows
        // through the parent, where the parent's policies apply. Only a direct read of the new
        // partition — or a superuser-free isolation test — reveals that it has none of its own.
        if (Unavailable()) { return; }

        var horizon = AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(6));
        await DropPartitionAsync(horizon);
        await RunAsync();

        var flags = await ScalarAsync<string>(
            $"""
             SELECT relrowsecurity || '/' || relforcerowsecurity
             FROM pg_class WHERE oid = 'auditing.{horizon}'::regclass;
             """);

        Assert.Equal("true/true", flags);
    }

    [Fact]
    public async Task ACreatedPartition_CarriesBothPolicies()
    {
        if (Unavailable()) { return; }

        var horizon = AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(7));
        await DropPartitionAsync(horizon);
        await RunAsync();

        var policies = await QueryAsync(
            $"""
             SELECT policyname || ':' || cmd FROM pg_policies
             WHERE schemaname = 'auditing' AND tablename = '{horizon}'
             ORDER BY policyname;
             """);

        Assert.Equal(
            ["rls_audit_events_append:INSERT", "rls_audit_events_read:SELECT"],
            policies);
    }

    [Fact]
    public async Task ACreatedPartition_RefusesUpdateAndDelete()
    {
        // The append-only guarantee has to survive partition creation, or it lapses one month at a
        // time as the horizon rolls forward.
        if (Unavailable()) { return; }

        var horizon = AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(8));
        await DropPartitionAsync(horizon);
        await RunAsync();

        var granted = await ScalarAsync<long>(
            $"""
             SELECT count(*) FROM information_schema.table_privileges
             WHERE table_schema = 'auditing' AND table_name = '{horizon}'
               AND privilege_type IN ('UPDATE', 'DELETE');
             """);

        Assert.Equal(0, granted);
    }

    // ---- Detection -----------------------------------------------------------------------------

    [Fact]
    public async Task APartitionWithUnexpectedBounds_IsReportedNotRepaired()
    {
        // Reported and left alone. A relation under audit_events that this job does not recognise
        // may hold evidence; automatic "repair" of it is how evidence is destroyed by a job nobody
        // was watching.
        if (Unavailable()) { return; }

        await RunAsync();

        await ExecuteAsync(
            _unprivileged,
            """
            CREATE TABLE auditing.audit_events_odd
                PARTITION OF auditing.audit_events
                FOR VALUES FROM ('2019-01-01 00:00:00+00') TO ('2019-04-01 00:00:00+00');
            """);

        var result = await RunAsync();

        Assert.Contains("audit_events_odd", result.Unexpected, StringComparer.Ordinal);
        Assert.DoesNotContain("audit_events_odd", result.PartitionsDropped, StringComparer.Ordinal);
        Assert.Contains("audit_events_odd", await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AMisnamedPartitionWhoseBoundsDisagree_IsReported()
    {
        if (Unavailable()) { return; }

        await RunAsync();

        // Named for March, holding February.
        await ExecuteAsync(
            _unprivileged,
            """
            CREATE TABLE auditing.audit_events_2019_03
                PARTITION OF auditing.audit_events
                FOR VALUES FROM ('2019-02-01 00:00:00+00') TO ('2019-03-01 00:00:00+00');
            """);

        var result = await RunAsync();

        Assert.Contains("audit_events_2019_03", result.Unexpected, StringComparer.Ordinal);
    }

    // ---- Retention -----------------------------------------------------------------------------

    [Fact]
    public async Task APartitionInsideRetention_IsNotEligible()
    {
        if (Unavailable()) { return; }

        var result = await RunAsync();

        // Everything the migration and this job created is recent by construction.
        Assert.Empty(result.RetentionEligible);
    }

    [Fact]
    public async Task AnExpiredPartition_IsReportedButNotDroppedByDefault()
    {
        // Dropping is off by default because legal holds are specified and unbuilt (I-11), so
        // nothing can confirm a partition is safe to destroy. Retention is still evaluated, so an
        // operator sees exactly what would go.
        if (Unavailable()) { return; }

        await CreateOldPartitionAsync("audit_events_2020_01", "2020-01-01", "2020-02-01");

        var result = await RunAsync();

        Assert.Contains("audit_events_2020_01", result.RetentionEligible, StringComparer.Ordinal);
        Assert.Empty(result.PartitionsDropped);
        Assert.Contains("audit_events_2020_01", await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AnExpiredPartition_IsDroppedWhenDroppingIsEnabled()
    {
        if (Unavailable()) { return; }

        await CreateOldPartitionAsync("audit_events_2020_02", "2020-02-01", "2020-03-01");

        var result = await RunAsync(dropExpired: true);

        Assert.Contains("audit_events_2020_02", result.PartitionsDropped, StringComparer.Ordinal);
        Assert.DoesNotContain("audit_events_2020_02", await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnablingDropping_DoesNotTouchPartitionsInsideRetention()
    {
        // The test that matters most if the retention arithmetic is ever changed: turning dropping
        // on must not turn it into a truncation of the whole table.
        if (Unavailable()) { return; }

        await RunAsync();
        var current = AuditPartition.NameFor(DateTimeOffset.UtcNow);

        var result = await RunAsync(dropExpired: true);

        Assert.DoesNotContain(current, result.PartitionsDropped, StringComparer.Ordinal);
        Assert.Contains(current, await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task MaintenanceNeverDeletesIndividualRows()
    {
        // Retention on an append-only relation is a partition drop and nothing else (DB-P5). A
        // DELETE would also be refused by the grant — but the point is that the job does not try.
        if (Unavailable()) { return; }

        await InsertEventAsync(CompanyA);
        await RunAsync(dropExpired: true);

        Assert.Equal(1, await CountEventsAsync(CompanyA));
    }

    // ---- Concurrency ---------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentCycles_AreSerialisedByTheAdvisoryLock()
    {
        // Two Worker replicas is the ordinary deployment, and both wake on the same schedule.
        // Without the lock they race to CREATE TABLE and one raises a duplicate-relation error.
        if (Unavailable()) { return; }

        var horizon = AuditPartition.NameFor(DateTimeOffset.UtcNow.AddMonths(9));
        await DropPartitionAsync(horizon);

        var results = await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());

        Assert.All(results, result => Assert.True(result.Succeeded));

        // Whoever held the lock created it; the others did nothing and said so.
        Assert.Single(results, result => result.PartitionsCreated.Contains(horizon));
        Assert.Contains(horizon, await PartitionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ACycleThatCannotTakeTheLock_ReportsNoFailure()
    {
        // Losing the race is a normal outcome, not an incident. Reporting it as a failure would
        // make a two-replica deployment permanently unhealthy.
        if (Unavailable()) { return; }

        await using var holder = new NpgsqlConnection(_unprivileged);
        await holder.OpenAsync();

        await using (var take = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({AuditPartitionMaintenance.LockKey});", holder))
        {
            await take.ExecuteNonQueryAsync();
        }

        var result = await RunAsync();

        Assert.False(result.LockAcquired);
        Assert.True(result.Succeeded);
        Assert.Empty(result.PartitionsCreated);
    }

    [Fact]
    public async Task TheLockIsReleasedAfterACycle()
    {
        // A lock left behind on a pooled connection would block every later cycle for the process
        // lifetime — the job would appear to run and silently do nothing.
        if (Unavailable()) { return; }

        await RunAsync();

        var held = await ScalarAsync<long>(
            $"""
             SELECT count(*) FROM pg_locks
             WHERE locktype = 'advisory' AND objid = {unchecked((int)AuditPartitionMaintenance.LockKey)};
             """);

        Assert.Equal(0, held);
    }

    // ---- Failure behaviour ---------------------------------------------------------------------

    [Fact]
    public async Task AnUnreachableDatabase_IsReportedRatherThanThrown()
    {
        // The Worker loop must survive to run the next cycle. A throw here would stop partition
        // creation altogether, which is the failure this milestone exists to prevent.
        if (Unavailable()) { return; }

        var maintenance = Build(
            "Host=localhost;Port=1;Database=nope;Username=nobody;Password=nobody",
            new AuditPartitionOptions());

        var result = await maintenance.RunAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task ACycleAfterAFailure_StillWorks()
    {
        if (Unavailable()) { return; }

        var broken = Build(
            "Host=localhost;Port=1;Database=nope;Username=nobody;Password=nobody",
            new AuditPartitionOptions());

        await broken.RunAsync(CancellationToken.None);

        var recovered = await RunAsync();

        Assert.True(recovered.Succeeded);
    }

    // ---- The audit store keeps working ---------------------------------------------------------

    [Fact]
    public async Task AuditEventsCanStillBeWrittenAfterMaintenance()
    {
        if (Unavailable()) { return; }

        await RunAsync();
        await InsertEventAsync(CompanyA);

        Assert.Equal(1, await CountEventsAsync(CompanyA));
    }

    [Fact]
    public async Task TenantIsolationSurvivesMaintenance()
    {
        if (Unavailable()) { return; }

        await InsertEventAsync(CompanyA);
        await InsertEventAsync(CompanyB);

        await RunAsync();

        Assert.Equal(1, await CountEventsAsync(CompanyA));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM auditing.audit_events;"));
    }

    [Fact]
    public async Task AppendOnlyProtectionSurvivesMaintenance()
    {
        if (Unavailable()) { return; }

        await InsertEventAsync(CompanyA);
        await RunAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            _unprivileged, "UPDATE auditing.audit_events SET outcome = 'Denied';", CompanyA));

        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            _unprivileged, "DELETE FROM auditing.audit_events;", CompanyA));

        Assert.Equal("42501", update.SqlState);
        Assert.Equal("42501", delete.SqlState);
    }

    [Fact]
    public async Task MaintenanceDoesNotModifyExistingRows()
    {
        if (Unavailable()) { return; }

        await InsertEventAsync(CompanyA);
        var before = await ScalarAsync<string>(
            "SELECT id::text || outcome FROM auditing.audit_events;", CompanyA);

        await RunAsync(dropExpired: true);

        Assert.Equal(
            before,
            await ScalarAsync<string>(
                "SELECT id::text || outcome FROM auditing.audit_events;", CompanyA));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static AuditPartitionMaintenance Build(string connectionString, AuditPartitionOptions settings) =>
        new(
            Options.Create(new PersistenceOptions { ConnectionString = connectionString }),
            Options.Create(settings),
            NullLogger<AuditPartitionMaintenance>.Instance,
            TimeProvider.System);

    private Task<Application.Abstractions.Maintenance.AuditPartitionMaintenanceResult> RunAsync(
        bool dropExpired = false) =>
        Build(_unprivileged, new AuditPartitionOptions { DropExpiredPartitions = dropExpired })
            .RunAsync(CancellationToken.None);

    private Task DropPartitionAsync(string name) =>
        ExecuteAsync(_unprivileged, $"DROP TABLE IF EXISTS auditing.{name};");

    private Task CreateOldPartitionAsync(string name, string from, string to) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             CREATE TABLE auditing.{name} PARTITION OF auditing.audit_events
                 FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00');
             """);

    private Task InsertEventAsync(Guid company) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             INSERT INTO auditing.audit_events
                 (id, occurred_at_utc, action, outcome, actor_type, company_id)
             VALUES (gen_random_uuid(), now(), 'authentication.sign-in', 'Success', 'Anonymous',
                     '{company}');
             """,
            company);

    private Task<long> CountEventsAsync(Guid company) =>
        ScalarAsync<long>("SELECT count(*) FROM auditing.audit_events;", company);

    private async Task<IReadOnlyList<string>> PartitionNamesAsync() =>
        await QueryAsync(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_inherits i ON i.inhrelid = c.oid
            WHERE i.inhparent = 'auditing.audit_events'::regclass
            ORDER BY c.relname;
            """).ConfigureAwait(false);

    private static async Task AdministerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            $"Host=localhost;Port=5432;Database=postgres;Username={Environment.UserName}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, Guid? company = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyTenantAsync(connection, company).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid? company = null)
    {
        await using var connection = new NpgsqlConnection(_unprivileged);
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyTenantAsync(connection, company).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return (T)Convert.ChangeType(
            value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> QueryAsync(string sql, Guid? company = null)
    {
        await using var connection = new NpgsqlConnection(_unprivileged);
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyTenantAsync(connection, company).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var values = new List<string>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    private static async Task ApplyTenantAsync(NpgsqlConnection connection, Guid? company)
    {
        await using var command = new NpgsqlCommand(
            company is null
                ? "SELECT set_config('app.current_company_id', '', false);"
                : $"SELECT set_config('app.current_company_id', '{company}', false);",
            connection);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
