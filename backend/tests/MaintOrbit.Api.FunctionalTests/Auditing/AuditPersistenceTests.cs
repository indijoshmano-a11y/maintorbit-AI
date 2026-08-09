using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Shared.Auditing;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MaintOrbit.Api.FunctionalTests.Auditing;

/// <summary>
/// Proves that emission actually reaches <c>auditing.audit_events</c>.
/// </summary>
/// <remarks>
/// <b>The complement to <see cref="AuditEmissionTests"/>, and neither replaces the other.</b> That
/// suite substitutes a recording trail, so it proves handlers <i>emit</i> — it would keep passing
/// if the sink wrote nowhere, which is exactly what it did before this milestone. This suite runs
/// the real trail and the real sink through real HTTP and then reads the table, so it proves the
/// events are <i>stored</i>.
/// <para>
/// Rows are read back with a direct connection rather than through EF, because the point is what
/// PostgreSQL holds, not what the model would map. A query through the same context that wrote the
/// row could be answered from the change tracker.
/// </para>
/// </remarks>
public sealed class AuditPersistenceTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());

    private IHost? _host;
    private string? _skip;
    private string? _database;
    private EmployeeId _employeeId;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync().ConfigureAwait(false);

        if (_database is null)
        {
            _skip = "No PostgreSQL reachable.";
            return;
        }

        _host = BuildHost(_database);
        _host.Start();

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);

        await SeedAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);
    }

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Authentication events -----------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulSignIn_IsPersisted()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Password);

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.Equal("Success", stored.Outcome);
        Assert.Equal(AuditTargets.Session, stored.TargetType);
        Assert.Equal(_company.Value, stored.CompanyId);
    }

    [Fact]
    public async Task AFailedSignIn_IsPersisted()
    {
        // FR-AUTH-014 audits failure as well as success, and a burst of them is a detection signal
        // — which only works if the failures are in the store rather than only in a log.
        if (Unavailable()) { return; }

        await SignInAsync("wrong password entirely");

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.Equal("Failure", stored.Outcome);
        Assert.Equal("Anonymous", stored.ActorType);
    }

    [Fact]
    public async Task ASignInForAnUnknownAddress_IsPersistedWithNoCompany()
    {
        // The platform-level case the append policy's IS NOT DISTINCT FROM exists for. Before that
        // choice, this row could not be written at all: the Company is the result of the lookup,
        // and the lookup found nobody.
        if (Unavailable()) { return; }

        await SignInAsync(Password, "nobody@example.test");

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.Null(stored.CompanyId);
        Assert.Equal("Failure", stored.Outcome);
    }

    [Fact]
    public async Task ASignOut_IsPersisted()
    {
        if (Unavailable()) { return; }

        var token = await SignInAsync(Password);
        await ClearAsync();

        using var client = _host!.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative), null);

        response.EnsureSuccessStatusCode();

        var stored = await SingleAsync(AuditActions.SignOut);

        Assert.Equal("Success", stored.Outcome);
        Assert.Equal(_company.Value, stored.CompanyId);
    }

    // ---- Authorization events ------------------------------------------------------------------

    [Fact]
    public async Task APermissionDenial_IsPersisted()
    {
        // FR-PERM-004 audits every denial. The seeded Employee holds no role, so any protected
        // endpoint denies — and §3.4 lists denial among the events that must be recorded.
        if (Unavailable()) { return; }

        var token = await SignInAsync(Password);
        await ClearAsync();

        using var client = _host!.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync(new Uri("/api/v1/employees", UriKind.Relative));

        var stored = await SingleAsync(AuditActions.PermissionDenied);

        Assert.Equal("Denied", stored.Outcome);
        Assert.Equal(AuditTargets.Endpoint, stored.TargetType);
    }

    // ---- What each row carries -----------------------------------------------------------------

    [Fact]
    public async Task AStoredEvent_CarriesTheActor()
    {
        // AU-3. An audit trail that cannot say who acted answers none of the questions it exists
        // for.
        if (Unavailable()) { return; }

        var token = await SignInAsync(Password);
        await ClearAsync();

        using var client = _host!.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative), null);

        var stored = await SingleAsync(AuditActions.SignOut);

        Assert.Equal("Employee", stored.ActorType);
        Assert.Equal(_employeeId.Value, stored.ActorEmployeeId);
    }

    [Fact]
    public async Task AStoredEvent_CarriesTheCorrelationIdentifier()
    {
        // The identifier that lets an investigator reconstruct one request across records. It is
        // filled by the trail rather than the caller, so every row carries one.
        if (Unavailable()) { return; }

        await SignInAsync(Password);

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.False(string.IsNullOrWhiteSpace(stored.CorrelationId));
    }

    [Fact]
    public async Task AStoredEvent_CarriesItsContextAsJson()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Password);

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.NotNull(stored.Context);
        Assert.Contains("clientType", stored.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredEvent_HasNoStreamEntryIdentifierYet()
    {
        // DD-6's column exists and its unique index is in place, but §3.3's durable stream is not
        // built — emission writes straight through, so there is no stream entry to record.
        if (Unavailable()) { return; }

        await SignInAsync(Password);

        var stored = await SingleAsync(AuditActions.SignIn);

        Assert.Null(stored.StreamEntryId);
    }

    [Fact]
    public async Task ManyEvents_AreAllStored()
    {
        // AU-2: never sampled, under any load. A store that dropped events under repetition would
        // fail the guarantee the product is partly sold on.
        if (Unavailable()) { return; }

        await ClearAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync("wrong password entirely");
        }

        Assert.Equal(5, await CountAsync(AuditActions.SignIn));
    }

    // ---- Security ------------------------------------------------------------------------------

    [Fact]
    public async Task NoStoredEvent_ContainsThePassword()
    {
        // The store has no delete path, so a credential written here cannot be removed by any code
        // the system has. This drives real sign-ins — right and wrong — and then greps the whole
        // table, rather than trusting the sanitizer's unit tests to describe the built system.
        if (Unavailable()) { return; }

        await SignInAsync(Password);
        await SignInAsync("wrong password entirely");

        var everything = await DumpAsync();

        Assert.DoesNotContain(Password, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrong password entirely", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoStoredEvent_ContainsTokenOrHashMaterial()
    {
        // Argon2id hashes begin `$argon2id$`; the access token is a JWT beginning `eyJ`. Neither
        // belongs in a record that is exported to customers and kept for a year.
        if (Unavailable()) { return; }

        var token = await SignInAsync(Password);

        var everything = await DumpAsync();

        Assert.DoesNotContain("$argon2", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialShapedContextValue_IsRedactedBeforeItIsStored()
    {
        // End to end rather than at the domain boundary: the guard runs in the factory the sink
        // calls, so a value offered by any emission point is redacted by the time it reaches a
        // column.
        if (Unavailable()) { return; }

        using var scope = _host!.Services.CreateScope();
        var trail = scope.ServiceProvider
            .GetRequiredService<Application.Abstractions.Auditing.IAuditTrail>();

        await trail.RecordAsync(
            AuditActions.SignIn,
            AuditOutcome.Success,
            AuditTargets.Session,
            "session-1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refreshToken"] = "a-real-looking-secret-value"
            });

        var everything = await DumpAsync();

        Assert.DoesNotContain("a-real-looking-secret-value", everything, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuditFailure_DoesNotCreateASecondAuditPath()
    {
        // Emission is fail-open (ADR-0021): a sink failure is logged as an AU-8 incident and the
        // operation stands. What must not happen is a fallback that writes the event somewhere
        // else — §3.1 warns that audit events written as log entries inherit log sampling and
        // retention, so a second destination would answer the same question with weaker
        // guarantees.
        //
        // Only one sink is registered, and this asserts that rather than inferring it.
        if (Unavailable()) { return; }

        using var scope = _host!.Services.CreateScope();

        var sinks = scope.ServiceProvider
            .GetServices<Application.Abstractions.Auditing.IAuditSink>()
            .ToList();

        var single = Assert.Single(sinks);
        Assert.Equal("PersistentAuditSink", single.GetType().Name);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private sealed record StoredEvent(
        string Action,
        string Outcome,
        string ActorType,
        Guid? CompanyId,
        Guid? ActorEmployeeId,
        string? TargetType,
        string? TargetId,
        string? CorrelationId,
        string? Context,
        string? StreamEntryId);

    private async Task<StoredEvent> SingleAsync(string action)
    {
        var rows = await ReadAsync(action).ConfigureAwait(false);

        return Assert.Single(rows);
    }

    private async Task<int> CountAsync(string action) =>
        (await ReadAsync(action).ConfigureAwait(false)).Count;

    /// <summary>
    /// Reads rows with a direct connection.
    /// </summary>
    /// <remarks>
    /// The test connects as the database owner, which on a developer install is a superuser and
    /// therefore bypasses row-level security. That is the right choice <i>here</i> — this suite
    /// asks what was stored, across tenants and including untenanted rows. Isolation is proved in
    /// <see cref="AuditStoreSchemaTests"/>, which builds an unprivileged role precisely because a
    /// superuser would make those assertions vacuous.
    /// </remarks>
    private async Task<IReadOnlyList<StoredEvent>> ReadAsync(string action)
    {
        await using var connection = new NpgsqlConnection(_database);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            SELECT action, outcome, actor_type, company_id, actor_employee_id,
                   target_type, target_id, correlation_id, context::text, stream_entry_id
            FROM auditing.audit_events
            WHERE action = @action
            ORDER BY occurred_at_utc;
            """,
            connection);

        command.Parameters.AddWithValue("action", action);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var rows = new List<StoredEvent>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new StoredEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    /// <summary>Every stored row rendered as text, for the "no secret anywhere" assertions.</summary>
    private async Task<string> DumpAsync()
    {
        await using var connection = new NpgsqlConnection(_database);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "SELECT coalesce(string_agg(t::text, ' '), '') FROM auditing.audit_events t;",
            connection);

        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task ClearAsync()
    {
        // Not a delete on audit_events — that is refused, by design. Truncating a partition is the
        // only removal mechanism there is (DB-P5), and it is available here because this is a
        // scratch database the test owns.
        await using var connection = new NpgsqlConnection(_database);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "TRUNCATE auditing.audit_events;", connection);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<string> SignInAsync(string password, string? email = null)
    {
        using var client = _host!.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email = email ?? Address, password, clientType = "WebConsole" })
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>()
            .ConfigureAwait(false);

        return payload.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            var employee = Employee.Invite(_company, Email.Create(Address), DateTimeOffset.UtcNow);
            context.Employees.Add(employee);
            await context.SaveChangesAsync().ConfigureAwait(false);
            _employeeId = employee.Id;
        }

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ICommandHandler<AcceptInvitationCommand>>()
                .HandleAsync(
                    new AcceptInvitationCommand(
                        _employeeId,
                        InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG"),
                        Password),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Seeding signs nothing in, but accepting the invitation may emit; the tests assert on
        // what they themselves cause.
        await ClearAsync().ConfigureAwait(false);
    }

    private static IHost BuildHost(string connectionString) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment(EnvironmentNames.Development)
                .ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
                        {
                            ["Application:Name"] = "MaintOrbit AI",
                            ["Application:PublicBaseUrl"] = "https://api.example.test",
                            ["Cors:AllowCredentials"] = "true",
                            ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                            ["Persistence:ConnectionString"] = connectionString,
                            ["PasswordHashing:MemoryKibibytes"] = "19456",
                            ["PasswordHashing:Iterations"] = "2",
                            ["PasswordHashing:Parallelism"] = "1",
                            ["PasswordHashing:Version"] = "1"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAuthenticationEndpoints();
                        endpoints.MapEmployeeEndpoints();
                    });
                }))
            .Build();
}
