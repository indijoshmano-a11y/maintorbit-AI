using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MaintOrbit.Api.FunctionalTests.Auditing;

/// <summary>
/// Verifies the applied <c>auditing.audit_events</c> schema and its security properties.
/// </summary>
/// <remarks>
/// <b>Everything here runs as a <c>NOSUPERUSER NOBYPASSRLS</c> role, and that is not incidental.</b>
/// A superuser bypasses row-level security unconditionally, so an isolation test run as one passes
/// whether or not the policies exist — it asserts nothing while looking thorough. The shared
/// <see cref="TestDatabase"/> connects as the developer's own account, which on a local install is
/// a superuser, so this suite builds its own role instead.
/// <para>
/// The same reason applies to <c>REVOKE UPDATE, DELETE</c>: privileges do not restrict a
/// superuser, so the append-only guarantee would appear to hold under a connection that could
/// ignore it.
/// </para>
/// </remarks>
public sealed class AuditStoreSchemaTests : IAsyncLifetime
{
    private const string Role = "maintorbit_audit_test";
    private const string RolePassword = "audit-test";

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
            await ExecuteAsAdministratorAsync(
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

        // Migrations run as the unprivileged role, so it owns the tables and the REVOKE in the
        // migration applies to it. That mirrors the deployment shape the migration assumes.
        await ExecuteAsync(owner, $"GRANT ALL ON DATABASE {databaseName} TO {Role};")
            .ConfigureAwait(false);
        await ExecuteAsync(owner, $"ALTER DATABASE {databaseName} OWNER TO {Role};")
            .ConfigureAwait(false);

        // The application's own provider configuration, so the migration is applied exactly as a
        // deployment would apply it — naming convention, history table, and all.
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
            await ExecuteAsAdministratorAsync($"DROP ROLE IF EXISTS {Role};").ConfigureAwait(false);
        }
    }

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        Assert.True(_skip is null || _skip.Length > 0);
    }

    [Fact]
    public async Task TheTestRole_CannotBypassRowLevelSecurity()
    {
        // The premise every other test here rests on. If this role were a superuser or carried
        // BYPASSRLS, the isolation and append-only assertions below would pass vacuously.
        if (Unavailable()) { return; }

        var bypasses = await ScalarAsync<bool>(
            "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user;")
            ;

        Assert.False(bypasses);
    }

    // ---- Schema --------------------------------------------------------------------------------

    [Fact]
    public async Task TheTable_IsPartitioned()
    {
        // DD-12: partitioned from the first migration, because retrofitting rewrites the table.
        if (Unavailable()) { return; }

        var relkind = await ScalarAsync<char>(
            "SELECT relkind FROM pg_class WHERE oid = 'auditing.audit_events'::regclass;")
            ;

        Assert.Equal('p', relkind);
    }

    [Fact]
    public async Task ThePartitions_AreMonthlyAndNamedByPeriod()
    {
        // §1.5 names partitions `<table>_<period>`. The migration creates one month behind through
        // twelve ahead, so a boundary crossing does not lose the ingestion path immediately.
        if (Unavailable()) { return; }

        var partitions = await QueryAsync(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'auditing' AND c.relispartition AND c.relkind = 'r'
            ORDER BY c.relname;
            """);

        Assert.Equal(14, partitions.Count);
        Assert.All(partitions, name => Assert.Matches(@"^audit_events_\d{4}_\d{2}$", name));
    }

    [Fact]
    public async Task TheColumns_AreTheDocumentedOnes()
    {
        // database-design §4.10's key columns, plus the composite primary key's two halves.
        if (Unavailable()) { return; }

        var columns = await QueryAsync(
            """
            SELECT a.attname FROM pg_attribute a
            WHERE a.attrelid = 'auditing.audit_events'::regclass
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attname;
            """);

        Assert.Equal(
            [
                "action", "actor_employee_id", "actor_type", "company_id", "context",
                "correlation_id", "id", "occurred_at_utc", "outcome", "stream_entry_id",
                "target_id", "target_type"
            ],
            columns);
    }

    [Fact]
    public async Task ThePrimaryKey_CarriesThePartitionKey()
    {
        // DD-2. PostgreSQL requires it, and stating it here means a later change that drops the
        // partitioning would fail loudly rather than quietly reverting to a single relation.
        if (Unavailable()) { return; }

        var definition = await ScalarAsync<string>(
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conname = 'pk_audit_events'
              AND conrelid = 'auditing.audit_events'::regclass;
            """);

        Assert.Equal("PRIMARY KEY (id, occurred_at_utc)", definition);
    }

    [Fact]
    public async Task TheDocumentedIndexes_Exist()
    {
        if (Unavailable()) { return; }

        var indexes = await QueryAsync(
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'auditing' AND tablename = 'audit_events'
            ORDER BY indexname;
            """);

        Assert.Equal(
            [
                "ix_audit_events_company_id_action_occurred_at_utc",
                "ix_audit_events_company_id_actor_employee_id_occurred_at_utc",
                "ix_audit_events_company_id_occurred_at_utc",
                "pk_audit_events",
                "ux_audit_events_stream_entry_id"
            ],
            indexes);
    }

    [Fact]
    public async Task TheStreamEntryIndex_IsUniqueAndCarriesThePartitionKey()
    {
        // DD-6 wants stream_entry_id unique; DD-12 wants the table partitioned; PostgreSQL refuses
        // a unique index on a partitioned table that omits the partition key. Including it keeps
        // DD-6's intent — redelivery replays the same entry at the same instant — and this test
        // records that the compromise is deliberate rather than a slip.
        if (Unavailable()) { return; }

        var definition = await ScalarAsync<string>(
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'auditing' AND indexname = 'ux_audit_events_stream_entry_id';
            """);

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("stream_entry_id", definition, StringComparison.Ordinal);
        Assert.Contains("occurred_at_utc", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVocabularyConstraints_CloseTheColumns()
    {
        if (Unavailable()) { return; }

        var constraints = await QueryAsync(
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'auditing.audit_events'::regclass AND contype = 'c'
            ORDER BY conname;
            """);

        Assert.Equal(
            [
                "ck_audit_events_actor_identified",
                "ck_audit_events_actor_type",
                "ck_audit_events_outcome"
            ],
            constraints);
    }

    // ---- Row-level security --------------------------------------------------------------------

    [Fact]
    public async Task RowLevelSecurity_IsEnabledAndForcedOnTheParent()
    {
        // ENABLE makes the policies apply; FORCE makes them apply to the owner, and migrations run
        // as owner. Without FORCE the policy exists, reads correctly, and filters nothing for the
        // account most likely to be used by a script.
        if (Unavailable()) { return; }

        var flags = await ScalarAsync<string>(
            """
            SELECT relrowsecurity || '/' || relforcerowsecurity
            FROM pg_class WHERE oid = 'auditing.audit_events'::regclass;
            """);

        // PostgreSQL renders booleans lower case when concatenated to text.
        Assert.Equal("true/true", flags);
    }

    [Fact]
    public async Task RowLevelSecurity_IsEnabledOnEveryPartition()
    {
        // A partition is a table. Policies on the parent apply when rows are reached through it,
        // which is how the application works — but a partition addressed directly answers to its
        // own. Without this, "every tenant-scoped relation carries a policy" would be true of the
        // parent and false of the fourteen relations actually holding rows.
        if (Unavailable()) { return; }

        var unprotected = await ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'auditing' AND c.relispartition AND c.relkind = 'r'
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
            """);

        Assert.Equal(0, unprotected);
    }

    [Fact]
    public async Task OnePolicyEach_ForReadingAndAppending()
    {
        // No UPDATE or DELETE policy, deliberately. Under FORCE, a command with no policy matches
        // no rows — the first of the two append-only mechanisms.
        if (Unavailable()) { return; }

        var policies = await QueryAsync(
            """
            SELECT policyname || ':' || cmd FROM pg_policies
            WHERE schemaname = 'auditing' AND tablename = 'audit_events'
            ORDER BY policyname;
            """);

        Assert.Equal(
            ["rls_audit_events_append:INSERT", "rls_audit_events_read:SELECT"],
            policies);
    }

    [Fact]
    public async Task ACompanySeesOnlyItsOwnEvents()
    {
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");
        await InsertAsync(CompanyB, "authentication.sign-out");

        var seenByA = await ScalarAsync<long>(
            "SELECT count(*) FROM auditing.audit_events;", CompanyA);
        var actionSeenByA = await ScalarAsync<string>(
            "SELECT action FROM auditing.audit_events;", CompanyA);

        Assert.Equal(1, seenByA);
        Assert.Equal("authentication.sign-in", actionSeenByA);
    }

    [Fact]
    public async Task AnUnsetTenant_SeesNothing()
    {
        // The documented failure direction: zero rows, never unfiltered rows (§5.2).
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var visible = await ScalarAsync<long>("SELECT count(*) FROM auditing.audit_events;")
            ;

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task AnUnsetTenant_SeesNothingThroughAPartitionEither()
    {
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var partition = await ScalarAsync<string>(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'auditing' AND c.relispartition AND c.relkind = 'r'
            ORDER BY c.relname DESC LIMIT 1;
            """);

        var visible = await ScalarAsync<long>($"SELECT count(*) FROM auditing.{partition};")
            ;

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task ACompanyCannotWriteAnEventBelongingToAnother()
    {
        // Injecting a record into another Company's evidence store is the tampering the policy
        // exists to stop, and it would be invisible to the Company it framed.
        if (Unavailable()) { return; }

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(CompanyB, "authentication.sign-in", asCompany: CompanyA));

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task APlatformEventIsWritableWithNoTenantInScope()
    {
        // A sign-in attempt against an unknown address has no Company, and those attempts are the
        // most security-relevant records the platform keeps. The append policy uses
        // IS NOT DISTINCT FROM precisely so this is possible.
        if (Unavailable()) { return; }

        await InsertAsync(null, "authentication.sign-in");

        var visibleToA = await ScalarAsync<long>(
            "SELECT count(*) FROM auditing.audit_events;", CompanyA);

        // Written, and invisible to every tenant — it belongs to none of them.
        Assert.Equal(0, visibleToA);
    }

    [Fact]
    public async Task ACompanyInScopeCannotWriteAnUntenantedEvent()
    {
        // The other half of IS NOT DISTINCT FROM. Without this, a tenant-scoped path could launder
        // a record out of its own Company by omitting the identifier.
        if (Unavailable()) { return; }

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(null, "authentication.sign-in", asCompany: CompanyA));

        Assert.Equal("42501", error.SqlState);
    }

    // ---- Append-only ---------------------------------------------------------------------------

    [Fact]
    public async Task UpdatingAnAuditEvent_IsRefused()
    {
        // DD-11's REVOKE, and the reason it is preferred to relying on the missing policy alone:
        // a revoked grant raises permission denied rather than silently reporting zero rows, so a
        // caller learns immediately instead of believing the edit worked.
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            _unprivileged,
            "UPDATE auditing.audit_events SET outcome = 'Denied';",
            CompanyA));

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task DeletingAnAuditEvent_IsRefused()
    {
        // Retention is a partition drop and nothing else (DB-P5). An audit record written in error
        // stays; corrections are compensating rows (§8.2).
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            _unprivileged,
            "DELETE FROM auditing.audit_events;",
            CompanyA));

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task UpdatingThroughAPartition_IsAlsoRefused()
    {
        // The REVOKE is applied per partition as well as on the parent, so addressing the storage
        // directly is not a way around it.
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var partition = await ScalarAsync<string>(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'auditing' AND c.relispartition AND c.relkind = 'r'
            ORDER BY c.relname LIMIT 1;
            """);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            _unprivileged,
            $"UPDATE auditing.{partition} SET outcome = 'Denied';",
            CompanyA));

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task NoUpdateOrDeleteGrantExists()
    {
        if (Unavailable()) { return; }

        var granted = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.table_privileges
            WHERE table_schema = 'auditing' AND table_name = 'audit_events'
              AND privilege_type IN ('UPDATE', 'DELETE');
            """);

        Assert.Equal(0, granted);
    }

    [Fact]
    public async Task InsertingRemainsPossible()
    {
        // The counterpart every append-only test needs: proving writes are refused is only
        // meaningful alongside proving the intended write still works.
        if (Unavailable()) { return; }

        await InsertAsync(CompanyA, "authentication.sign-in");

        var stored = await ScalarAsync<long>(
            "SELECT count(*) FROM auditing.audit_events;", CompanyA);

        Assert.Equal(1, stored);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private Task InsertAsync(Guid? company, string action, Guid? asCompany = null) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             INSERT INTO auditing.audit_events
                 (id, occurred_at_utc, action, outcome, actor_type, company_id)
             VALUES (gen_random_uuid(), now(), '{action}', 'Success', 'Anonymous',
                     {(company is { } c ? $"'{c}'" : "NULL")});
             """,
            asCompany ?? company);

    private static async Task ExecuteAsAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            $"Host=localhost;Port=5432;Database=postgres;Username={Environment.UserName}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        string connectionString, string sql, Guid? company = null)
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
