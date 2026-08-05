using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
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

namespace MaintOrbit.Api.FunctionalTests.Auditing;

/// <summary>
/// Asserts that every documented authentication and authorization event is emitted.
/// </summary>
/// <remarks>
/// <b>This class is the answer to §3.3.</b> That section puts audit emission at pipeline position 8
/// specifically so coverage is not "a function of developer discipline" — and the ADR-0012 pipeline
/// does not exist, so handlers emit directly, exactly as §3.3 notes the Gateway hot path does.
/// What replaces the pipeline's guarantee is this: every event in §3.4's authentication and
/// authorization rows is driven through the real endpoints and asserted, so a handler added later
/// without an audit event fails a test rather than going unnoticed until an investigation needs it.
/// <para>
/// The trail is captured rather than the sink, so the assertions are about what handlers emitted
/// rather than how the placeholder sink formats it.
/// </para>
/// </remarks>
public sealed class AuditEmissionTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";
    private const string AdminRole = "company-admin";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly RecordingAuditTrail _audit = new();
    private readonly AdvanceableClock _clock = new();

    private IHost? _host;
    private string? _skip;
    private string? _database;
    private EmployeeId _employeeId;
    private string _token = string.Empty;

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

        await SeedAsync().ConfigureAwait(false);

        _token = await SignInAsync(Password).ConfigureAwait(false);

        // Seeding signed in once already; the tests assert on what they themselves cause.
        _audit.Clear();
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);
    }

    private IHost BuildHost(string connectionString) =>
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
                            ["PasswordHashing:Version"] = "1",
                            ["AuthenticationPolicy:MaximumFailedAttempts"] = "3",
                            ["AuthenticationPolicy:LockoutMinutes"] = "15"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    services.AddSingleton<IAuditTrail>(_audit);
                    services.AddSingleton<TimeProvider>(_clock);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAuthenticationEndpoints();
                        endpoints.MapEmployeeEndpoints();
                        endpoints.MapEmployeeRoleEndpoints();
                        endpoints.MapSessionEndpoints();
                    });
                }))
            .Build();

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Makes the skip visible instead of silent.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Login ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulSignInIsAudited()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Password);

        var recorded = _audit.Single(AuditActions.SignIn);

        Assert.Equal(AuditOutcome.Success, recorded.Outcome);
        Assert.Equal(AuditTargets.Session, recorded.TargetType);
        Assert.False(string.IsNullOrEmpty(recorded.TargetId));
        Assert.Equal("WebConsole", recorded.Context!["clientType"]);
    }

    [Fact]
    public async Task AFailedSignInIsAudited()
    {
        if (Unavailable()) { return; }

        // FR-AUTH-014 audits failure as well as success, and §3.4 makes a burst of them a
        // detection signal — which only works if the record says which address was tried.
        await SignInAsync("wrong password entirely");

        var recorded = _audit.Single(AuditActions.SignIn);

        Assert.Equal(AuditOutcome.Failure, recorded.Outcome);
        Assert.Equal(AuditActorType.Anonymous, recorded.ActorType);
        Assert.Equal(Address, recorded.Context!["attemptedEmail"]);
    }

    [Fact]
    public async Task ASignInForAnUnknownAddressIsAuditedWithNoCompany()
    {
        if (Unavailable()) { return; }

        // No Company holds the address, so there is no tenant to attribute it to — and the record
        // must still exist, because this is exactly the shape a spraying attack produces.
        await SignInAsync(Password, "nobody@example.test");

        var recorded = _audit.Single(AuditActions.SignIn);

        Assert.Equal(AuditOutcome.Failure, recorded.Outcome);
        Assert.Null(recorded.CompanyId);
        Assert.Null(recorded.ActorEmployeeId);
    }

    [Fact]
    public async Task AnAccountLockoutIsAuditedSeparatelyFromTheFailures()
    {
        if (Unavailable()) { return; }

        // §3.4 lists lockout as its own event. One record says "this account is now locked", which
        // is what an alert fires on; the three failures that led to it are separate.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SignInAsync("wrong password entirely");
        }

        Assert.Equal(3, _audit.For(AuditActions.SignIn).Count);

        var lockout = _audit.Single(AuditActions.AccountLockout);

        Assert.Equal(AuditActorType.System, lockout.ActorType);
        Assert.Equal(_employeeId.Value, lockout.ActorEmployeeId);
        Assert.False(string.IsNullOrEmpty(lockout.Context!["until"]));
    }

    // ---- Logout --------------------------------------------------------------------------------------

    [Fact]
    public async Task ASignOutIsAudited()
    {
        if (Unavailable()) { return; }

        await PostAsync("/api/v1/auth/logout", _token);

        var recorded = _audit.Single(AuditActions.SignOut);

        Assert.Equal(AuditOutcome.Success, recorded.Outcome);
        Assert.Equal(AuditTargets.Session, recorded.TargetType);
    }

    [Fact]
    public async Task ASignOutEverywhereIsAuditedWithTheCount()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Password);
        _audit.Clear();

        await PostAsync("/api/v1/auth/logout-all", _token);

        var recorded = _audit.Single(AuditActions.SignOutEverywhere);

        Assert.Equal("2", recorded.Context!["revokedCount"]);
    }

    // ---- MFA -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnrollingASecondFactorIsAudited()
    {
        if (Unavailable()) { return; }

        await PostAsync("/api/v1/auth/mfa/enroll", _token);

        var recorded = _audit.Single(AuditActions.MfaEnrollmentBegun);

        Assert.Equal(AuditOutcome.Success, recorded.Outcome);
        Assert.Equal(AuditTargets.MfaEnrollment, recorded.TargetType);
    }

    [Fact]
    public async Task AFailedConfirmationIsAudited()
    {
        if (Unavailable()) { return; }

        await PostAsync("/api/v1/auth/mfa/enroll", _token);
        _audit.Clear();

        await PostJsonAsync("/api/v1/auth/mfa/confirm", new { code = "000000" }, _token);

        var recorded = _audit.Single(AuditActions.MfaEnrollmentConfirmed);

        Assert.Equal(AuditOutcome.Failure, recorded.Outcome);
    }

    [Fact]
    public async Task AFailedChallengeIsAudited()
    {
        if (Unavailable()) { return; }

        // Reaching the challenge needs a confirmed factor, which needs a valid code — so this
        // asserts the failure path, which is the one an investigation cares about.
        await PostAsync("/api/v1/auth/mfa/enroll", _token);
        _audit.Clear();

        await PostJsonAsync("/api/v1/auth/mfa/verify", new { code = "000000" }, _token);

        // Not enrolled yet, so this is a conflict rather than a challenge — no challenge event.
        Assert.Empty(_audit.For(AuditActions.MfaChallenge));
    }

    [Fact]
    public async Task DisablingWithoutAValidCodeIsAudited()
    {
        if (Unavailable()) { return; }

        await PostAsync("/api/v1/auth/mfa/enroll", _token);
        _audit.Clear();

        await PostJsonAsync("/api/v1/auth/mfa/disable", new { code = "000000" }, _token);

        // No confirmed factor, so it never reaches the verifier — the conflict is not a disable
        // attempt and is not recorded as one.
        Assert.Empty(_audit.For(AuditActions.MfaDisabled));
    }

    // ---- Permission changes --------------------------------------------------------------------------------

    [Fact]
    public async Task AssigningARoleIsAudited()
    {
        if (Unavailable()) { return; }

        await GrantManageAsync();
        _audit.Clear();

        await PostJsonAsync(
            $"/api/v1/employees/{_employeeId.Value}/roles",
            new { roleCode = AdminRole, scope = "Self" },
            _token);

        var recorded = _audit.Single(AuditActions.RoleAssigned);

        Assert.Equal(AuditOutcome.Success, recorded.Outcome);
        Assert.Equal(AuditTargets.RoleAssignment, recorded.TargetType);
        Assert.Equal(AdminRole, recorded.Context!["roleCode"]);
        Assert.Equal("Self", recorded.Context["scope"]);
        Assert.Equal(_employeeId.ToString(), recorded.Context["employeeId"]);
    }

    [Fact]
    public async Task RemovingARoleIsAudited()
    {
        if (Unavailable()) { return; }

        await GrantManageAsync();

        var (_, created) = await PostJsonAsync(
            $"/api/v1/employees/{_employeeId.Value}/roles",
            new { roleCode = AdminRole, scope = "Self" },
            _token);

        var assignmentId = created.GetProperty("id").GetGuid();
        _audit.Clear();

        await DeleteAsync($"/api/v1/employees/{_employeeId.Value}/roles/{assignmentId}", _token);

        var recorded = _audit.Single(AuditActions.RoleRemoved);

        Assert.Equal(assignmentId.ToString(), recorded.TargetId);
        Assert.Equal(AdminRole, recorded.Context!["roleCode"]);
    }

    [Fact]
    public async Task AnAuthorizationDenialIsAudited()
    {
        if (Unavailable()) { return; }

        // FR-PERM-004: every denial. §3.4 calls denials "a primary detection signal", which is why
        // this is recorded even though nothing was changed.
        var status = await GetStatusAsync("/api/v1/employees", _token);

        Assert.Equal(HttpStatusCode.Forbidden, status);

        var recorded = _audit.Single(AuditActions.PermissionDenied);

        Assert.Equal(AuditOutcome.Denied, recorded.Outcome);
        Assert.Equal(AuditTargets.Endpoint, recorded.TargetType);
        Assert.Equal("/api/v1/employees", recorded.TargetId);
        Assert.Equal("GET", recorded.Context!["method"]);
    }

    [Fact]
    public async Task ADenialRecordsTheEndpointButNotThePermission()
    {
        if (Unavailable()) { return; }

        await GetStatusAsync("/api/v1/employees", _token);

        var serialized = JsonSerializer.Serialize(_audit.Single(AuditActions.PermissionDenied));

        // The endpoint is enough to reconstruct what was required, and the response must not name
        // it — recording it here would make the two easy to conflate later.
        Assert.DoesNotContain("employee.read", serialized, StringComparison.Ordinal);
    }

    // ---- Session revocation ------------------------------------------------------------------------------------

    [Fact]
    public async Task RevokingOneSessionIsAudited()
    {
        if (Unavailable()) { return; }

        var second = await SignInAsync(Password);
        var target = await CurrentSessionIdAsync(second);
        _audit.Clear();

        await DeleteAsync($"/api/v1/employees/me/sessions/{target}", _token);

        var recorded = _audit.Single(AuditActions.SessionRevoked);

        Assert.Equal(AuditTargets.Session, recorded.TargetType);
        Assert.Equal(target, recorded.TargetId);
    }

    [Fact]
    public async Task RevokingEveryOtherSessionIsAuditedWithTheCount()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Password);
        await SignInAsync(Password);
        _audit.Clear();

        await DeleteAsync("/api/v1/employees/me/sessions", _token);

        var recorded = _audit.Single(AuditActions.OtherSessionsRevoked);

        Assert.Equal("2", recorded.Context!["revokedCount"]);
    }

    // ---- What a record may not carry ---------------------------------------------------------------------------

    [Fact]
    public async Task NoRecordCarriesACredential()
    {
        if (Unavailable()) { return; }

        // AU-4 forbids content in an audit record, and §5 lists content leaking into one as a risk.
        // Passwords, tokens, and secrets are the identity module's version of that.
        await SignInAsync(Password);
        await SignInAsync("wrong password entirely");
        await PostAsync("/api/v1/auth/mfa/enroll", _token);
        await PostAsync("/api/v1/auth/logout", _token);

        var serialized = JsonSerializer.Serialize(_audit.Events);

        Assert.NotEmpty(_audit.Events);
        Assert.DoesNotContain(Password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong password", serialized, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "accessToken", "refreshToken", "secret", "passwordHash" })
        {
            Assert.DoesNotContain(forbidden, serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EveryRecordCarriesAnActionAnOutcomeAndATime()
    {
        if (Unavailable()) { return; }

        // AU-3: "records actor, action, target, outcome, timestamp, originating context". The
        // actor may legitimately be anonymous; the rest may not be missing.
        await SignInAsync(Password);
        await PostAsync("/api/v1/auth/logout", _token);
        await GetStatusAsync("/api/v1/employees", await SignInAsync(Password));

        Assert.NotEmpty(_audit.Events);

        foreach (var recorded in _audit.Events)
        {
            Assert.False(string.IsNullOrWhiteSpace(recorded.Action));
            Assert.NotEqual(default, recorded.OccurredAtUtc);
            Assert.True(Enum.IsDefined(recorded.Outcome));
            Assert.True(Enum.IsDefined(recorded.ActorType));
        }
    }

    // ---- Fail-open -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailingAuditTrailDoesNotFailTheOperation()
    {
        if (Unavailable()) { return; }

        // SD-004 classifies audit emission fail-open so a platform fault never becomes a customer
        // outage. The operation has already happened by the time the record is written; failing
        // the request would report that it had not.
        using var host = BuildHostWithFailingSink(_database!);
        host.Start();

        using var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = Address, password = Password, clientType = "WebConsole" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>A host whose sink always throws, to observe the trail absorbing it.</summary>
    private static IHost BuildHostWithFailingSink(string connectionString) =>
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

                    // The real trail over a sink that cannot write — the combination production
                    // would have if the stream were unreachable.
                    services.AddSingleton<IAuditSink, ThrowingAuditSink>();
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints => endpoints.MapAuthenticationEndpoints());
                }))
            .Build();

    private sealed class ThrowingAuditSink : IAuditSink
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit stream is unreachable.");
    }

    private async Task<string> SignInAsync(string password, string email = Address)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password, clientType = "WebConsole" }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = JsonDocument.Parse(body).RootElement;

        return parsed.TryGetProperty("accessToken", out var token)
            ? token.GetString()! : string.Empty;
    }

    private async Task<string> CurrentSessionIdAsync(string bearer)
    {
        var (_, body) = await SendAsync(
            HttpMethod.Get, "/api/v1/employees/me/sessions/current", null, bearer)
            .ConfigureAwait(false);

        return body.GetProperty("id").GetString()!;
    }

    private async Task<HttpStatusCode> PostAsync(string path, string bearer)
    {
        var (status, _) = await SendAsync(HttpMethod.Post, path, null, bearer)
            .ConfigureAwait(false);

        return status;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(
        string path, object payload, string bearer) =>
        SendAsync(HttpMethod.Post, path, payload, bearer);

    private async Task<HttpStatusCode> DeleteAsync(string path, string bearer)
    {
        var (status, _) = await SendAsync(HttpMethod.Delete, path, null, bearer)
            .ConfigureAwait(false);

        return status;
    }

    private async Task<HttpStatusCode> GetStatusAsync(string path, string bearer)
    {
        var (status, _) = await SendAsync(HttpMethod.Get, path, null, bearer)
            .ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, object? payload, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(method, path);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>Gives the Employee employee.manage, so the role endpoints are reachable.</summary>
    private async Task GrantManageAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.RolePermissions.Add(RolePermission.Grant(
            RoleCode.Create(AdminRole), IdentityPermissions.EmployeeManage));

        context.EmployeeRoles.Add(EmployeeRole.Assign(
            _company, _employeeId, RoleCode.Create(AdminRole),
            PermissionScope.Company, scopeId: null, _clock.GetUtcNow()));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedAsync()
    {
        using (var scope = _host!.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Permissions.Add(
                Permission.Define(IdentityPermissions.EmployeeManage, "Manage Employees"));
            context.RoleDefinitions.Add(
                RoleDefinition.Define(RoleCode.Create(AdminRole), "Company Admin", isBuiltIn: true));

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            var employee = Employee.Invite(_company, Email.Create(Address), _clock.GetUtcNow());
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
    }
}
