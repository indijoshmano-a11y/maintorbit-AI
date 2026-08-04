using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Drives the Company authentication policy through the real HTTP pipeline.
/// </summary>
/// <remarks>
/// <b>What matters here is that the policy is read, not that it is stored.</b> A settings table
/// nothing consults is the failure mode this class exists to catch, so the assertions go through
/// the paths that consume it: a password being set, and a session being opened.
/// <para>
/// Two Companies, so a policy set by one is observed not to reach the other. Skipped when no
/// PostgreSQL is reachable.
/// </para>
/// </remarks>
public sealed class AuthenticationPolicyTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string AdminRole = "company-admin";

    /// <summary>A login role that cannot bypass row-level security.</summary>
    private const string RlsProbeRole = "maintorbit_policy_probe";

    private readonly CompanyId _companyA = new(Guid.CreateVersion7());
    private readonly CompanyId _companyB = new(Guid.CreateVersion7());

    private IHost? _host;
    private string? _skip;
    private string? _database;

    private EmployeeId _adaId;
    private string _adaToken = string.Empty;
    private string _beaToken = string.Empty;

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

        await MigrateAndSeedCatalogueAsync().ConfigureAwait(false);

        _adaId = await SeedEmployeeAsync(_companyA, "ada@a.test").ConfigureAwait(false);
        var beaId = await SeedEmployeeAsync(_companyB, "bea@b.test").ConfigureAwait(false);

        await SeedAssignmentAsync(_companyA, _adaId).ConfigureAwait(false);
        await SeedAssignmentAsync(_companyB, beaId).ConfigureAwait(false);

        _adaToken = await SignInAsync("ada@a.test", Password).ConfigureAwait(false);
        _beaToken = await SignInAsync("bea@b.test", Password).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await DropProbeRoleAsync().ConfigureAwait(false);
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);
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
                        endpoints.MapAuthenticationPolicyEndpoints();
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

    // ---- Reading ---------------------------------------------------------------------------------

    [Fact]
    public async Task ACompanyWithNoPolicyGetsTheDeploymentDefaults()
    {
        if (Unavailable()) { return; }

        // Absence is not "unconfigured" — it is the defaults. The flag is what distinguishes the
        // two, because the values are identical either way.
        var (status, body) = await GetAsync(_adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("isCompanyConfigured").GetBoolean());
        Assert.Equal(12, body.GetProperty("minimumPasswordLength").GetInt32());
        Assert.Equal(60, body.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal(720, body.GetProperty("absoluteLifetimeMinutes").GetInt32());
        Assert.False(body.GetProperty("mfaRequired").GetBoolean());
        Assert.Equal(5, body.GetProperty("maximumFailedAttempts").GetInt32());
        Assert.Equal(15, body.GetProperty("lockoutMinutes").GetInt32());
    }

    [Fact]
    public async Task ASavedPolicyIsReportedAsConfigured()
    {
        if (Unavailable()) { return; }

        await PutAsync(Policy(minimumPasswordLength: 20), _adaToken);

        var (_, body) = await GetAsync(_adaToken);

        Assert.True(body.GetProperty("isCompanyConfigured").GetBoolean());
        Assert.Equal(20, body.GetProperty("minimumPasswordLength").GetInt32());
    }

    // ---- Writing ---------------------------------------------------------------------------------

    [Fact]
    public async Task SavingAPolicyReturnsWhatWasSaved()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PutAsync(
            Policy(minimumPasswordLength: 16, idleTimeoutMinutes: 30,
                absoluteLifetimeMinutes: 480, mfaRequired: true,
                maximumFailedAttempts: 3, lockoutMinutes: 60),
            _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(16, body.GetProperty("minimumPasswordLength").GetInt32());
        Assert.Equal(30, body.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal(480, body.GetProperty("absoluteLifetimeMinutes").GetInt32());
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.Equal(3, body.GetProperty("maximumFailedAttempts").GetInt32());
        Assert.Equal(60, body.GetProperty("lockoutMinutes").GetInt32());
    }

    [Fact]
    public async Task SavingTwiceUpdatesTheSameRow()
    {
        if (Unavailable()) { return; }

        await PutAsync(Policy(minimumPasswordLength: 16), _adaToken);
        await PutAsync(Policy(minimumPasswordLength: 24), _adaToken);

        var (_, body) = await GetAsync(_adaToken);

        Assert.Equal(24, body.GetProperty("minimumPasswordLength").GetInt32());

        // One row per Company. A second would be a state nothing could resolve, since neither
        // would be more current than the other.
        Assert.Equal(1, await StoredPolicyCountAsync(_companyA));
    }

    [Fact]
    public async Task TheActorIsRecordedOnAnUpdate()
    {
        if (Unavailable()) { return; }

        await PutAsync(Policy(), _adaToken);
        await PutAsync(Policy(minimumPasswordLength: 16), _adaToken);

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_companyA);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var stored = await context.CompanyAuthenticationPolicies.SingleAsync();

        Assert.Equal(_adaId, stored.UpdatedByEmployeeId);
    }

    // ---- Validation --------------------------------------------------------------------------------

    [Theory]
    [InlineData(4)]
    [InlineData(11)]
    [InlineData(200)]
    public async Task APasswordLengthOutsideItsBoundsIsRefused(int length)
    {
        if (Unavailable()) { return; }

        var (status, body) = await PutAsync(Policy(minimumPasswordLength: length), _adaToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnAbsoluteLifetimeShorterThanTheIdleWindowIsRefused()
    {
        if (Unavailable()) { return; }

        // The relational rule, which no field range can express — so it comes from the aggregate
        // rather than from the request contract.
        var (status, body) = await PutAsync(
            Policy(idleTimeoutMinutes: 600, absoluteLifetimeMinutes: 60), _adaToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(
            "absolute lifetime", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(50)]
    public async Task ALockoutThresholdOutsideItsBoundsIsRefused(int attempts)
    {
        if (Unavailable()) { return; }

        var (status, _) = await PutAsync(Policy(maximumFailedAttempts: attempts), _adaToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task ARefusedSaveLeavesThePolicyThatWasInForce()
    {
        if (Unavailable()) { return; }

        await PutAsync(Policy(minimumPasswordLength: 20), _adaToken);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PutAsync(Policy(minimumPasswordLength: 4), _adaToken)).Status);

        var (_, body) = await GetAsync(_adaToken);

        Assert.Equal(20, body.GetProperty("minimumPasswordLength").GetInt32());
    }

    [Fact]
    public async Task TheDatabaseRefusesAnOutOfBoundsPolicyToo()
    {
        if (Unavailable()) { return; }

        // The aggregate is the rule; the check constraints are the guarantee. A policy is read by
        // code that trusts it, so a row written by a script must not be able to widen a control
        // the aggregate refuses to widen.
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_companyA);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        // PostgresException rather than DbUpdateException: EF wraps a failure from SaveChanges,
        // and this deliberately bypasses SaveChanges to write the row the way a script would.
        var violation = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO identity.company_authentication_policies
                 (company_id, minimum_password_length, require_breach_check, idle_timeout_minutes,
                  absolute_lifetime_minutes, mfa_required, maximum_failed_attempts, lockout_minutes,
                  created_at_utc, updated_at_utc, row_version)
             VALUES ({_companyA.Value}, 1, true, 60, 720, false, 5, 15, now(), now(), 0)
             """));

        Assert.Equal(
            "ck_company_authentication_policies_password_length", violation.ConstraintName);
    }

    // ---- The policy is actually read -----------------------------------------------------------------

    [Fact]
    public async Task ThePasswordPolicyGovernsInvitationAcceptance()
    {
        if (Unavailable()) { return; }

        // FR-AUTH-002. A settings table nothing consults is the failure this asserts against.
        await PutAsync(Policy(minimumPasswordLength: 32), _adaToken);

        var invited = await InviteAsync(_companyA, "new@a.test");

        var tooShort = await AcceptAsync(_companyA, invited, "short-but-over-twelve");
        Assert.True(tooShort.IsFailure);
        Assert.Contains("32", tooShort.Error.Description, StringComparison.Ordinal);

        var longEnough = await AcceptAsync(
            _companyA, invited, "a password of at least thirty-two characters");
        Assert.True(longEnough.IsSuccess);
    }

    [Fact]
    public async Task ThePasswordPolicyIsTheCompanysOwn()
    {
        if (Unavailable()) { return; }

        // Company A demands 32; Company B has set nothing and gets the default 12. The same
        // password is refused in one and accepted in the other.
        await PutAsync(Policy(minimumPasswordLength: 32), _adaToken);

        var inA = await InviteAsync(_companyA, "one@a.test");
        var inB = await InviteAsync(_companyB, "one@b.test");

        Assert.True((await AcceptAsync(_companyA, inA, Password)).IsFailure);
        Assert.True((await AcceptAsync(_companyB, inB, Password)).IsSuccess);
    }

    [Fact]
    public async Task TheSessionPolicyGovernsAnOpenedSession()
    {
        if (Unavailable()) { return; }

        // FR-AUTH-007. The absolute lifetime is stored on the session, so the policy in force at
        // sign-in is observable on the row rather than only in a timer nobody can see.
        await PutAsync(Policy(idleTimeoutMinutes: 30, absoluteLifetimeMinutes: 45), _adaToken);

        var before = DateTimeOffset.UtcNow;
        await SignInAsync("ada@a.test", Password);

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_companyA);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var latest = await context.Sessions
            .Where(session => session.EmployeeId == _adaId)
            .OrderByDescending(session => session.CreatedAtUtc)
            .FirstAsync();

        // 45 minutes, not the deployment's 720.
        Assert.True(latest.AbsoluteExpiresAtUtc <= before.AddMinutes(46));
        Assert.True(latest.AbsoluteExpiresAtUtc > before.AddMinutes(44));
    }

    // ---- Authorization and isolation --------------------------------------------------------------------

    [Fact]
    public async Task BothEndpointsRequireThePermission()
    {
        if (Unavailable()) { return; }

        // Company B's Employee holds the same role, but that role grants no company.manage.
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(_beaToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(Policy(), _beaToken)).Status);
    }

    [Fact]
    public async Task BothEndpointsRequireAuthentication()
    {
        if (Unavailable()) { return; }

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(Policy(), null)).Status);
    }

    [Fact]
    public async Task APolicyIsInvisibleToAnotherCompany()
    {
        if (Unavailable()) { return; }

        // Asserted against a NOSUPERUSER NOBYPASSRLS role: the account these tests connect as is a
        // superuser and PostgreSQL exempts it from every policy, so an endpoint-level assertion
        // would pass whether the row-level policy existed or not.
        await PutAsync(Policy(minimumPasswordLength: 20), _adaToken);

        const string Query = "SELECT count(*) FROM identity.company_authentication_policies";

        Assert.Equal(1, await AsRestrictedRoleAsync(_companyA, Query));
        Assert.Equal(0, await AsRestrictedRoleAsync(_companyB, Query));
    }

    [Fact]
    public async Task AResponseRunsThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/company/authentication-policy");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adaToken);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static object Policy(
        int minimumPasswordLength = 12,
        int idleTimeoutMinutes = 60,
        int absoluteLifetimeMinutes = 720,
        bool mfaRequired = false,
        int maximumFailedAttempts = 5,
        int lockoutMinutes = 15) =>
        new
        {
            minimumPasswordLength,
            requireBreachCheck = true,
            idleTimeoutMinutes,
            absoluteLifetimeMinutes,
            mfaRequired,
            maximumFailedAttempts,
            lockoutMinutes
        };

    private Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string? bearer) =>
        SendAsync(HttpMethod.Get, null, bearer);

    private Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(object policy, string? bearer) =>
        SendAsync(HttpMethod.Put, policy, bearer);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, object? payload, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            method, "/api/v1/company/authentication-policy");

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<string> SignInAsync(string email, string password)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password, clientType = "WebConsole" }).ConfigureAwait(false);

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task MigrateAndSeedCatalogueAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.Database.MigrateAsync().ConfigureAwait(false);

        context.Permissions.Add(
            Permission.Define(IdentityPermissions.CompanyManage, "Manage Company settings"));
        context.RoleDefinitions.Add(
            RoleDefinition.Define(RoleCode.Create(AdminRole), "Company Admin", isBuiltIn: true));

        await context.SaveChangesAsync().ConfigureAwait(false);

        // Only Company A's role carries the permission — the grant is platform-wide reference
        // data, so isolation between the two comes from the assignment, not the grant.
        context.RolePermissions.Add(RolePermission.Grant(
            RoleCode.Create(AdminRole), IdentityPermissions.CompanyManage));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<EmployeeId> SeedEmployeeAsync(CompanyId company, string address)
    {
        var employeeId = await InviteAsync(company, address).ConfigureAwait(false);

        var accepted = await AcceptAsync(company, employeeId, Password).ConfigureAwait(false);

        Assert.True(accepted.IsSuccess);

        return employeeId;
    }

    private async Task<EmployeeId> InviteAsync(CompanyId company, string address)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var employee = Employee.Invite(company, Email.Create(address), DateTimeOffset.UtcNow);
        context.Employees.Add(employee);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return employee.Id;
    }

    /// <summary>
    /// Accepts an invitation through the real handler, so the policy check runs.
    /// </summary>
    /// <remarks>
    /// There is no invitation endpoint yet — §3.2 lists "invite" among the operations and it is not
    /// built — so this drives the use case directly. The policy is read inside the handler either
    /// way, which is what is being asserted.
    /// </remarks>
    private async Task<Domain.Common.Results.Result> AcceptAsync(
        CompanyId company, EmployeeId employeeId, string password)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ICommandHandler<AcceptInvitationCommand>>()
            .HandleAsync(
                new AcceptInvitationCommand(
                    employeeId,
                    InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG"),
                    password),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task SeedAssignmentAsync(CompanyId company, EmployeeId employee)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        // Company B's Employee is deliberately given no role, so the permission tests measure the
        // permission rather than the tenant.
        if (company == _companyA)
        {
            context.EmployeeRoles.Add(EmployeeRole.Assign(
                company, employee, RoleCode.Create(AdminRole),
                PermissionScope.Company, scopeId: null, DateTimeOffset.UtcNow));

            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> StoredPolicyCountAsync(CompanyId company)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.CompanyAuthenticationPolicies.CountAsync().ConfigureAwait(false);
    }

    private async Task<int> AsRestrictedRoleAsync(CompanyId company, string query)
    {
        var builder = new NpgsqlConnectionStringBuilder(_database!);

        await using (var admin = new NpgsqlConnection(_database))
        {
            await admin.OpenAsync().ConfigureAwait(false);

            await using var grant = new NpgsqlCommand(
                $"""
                 DO $$ BEGIN
                     IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RlsProbeRole}') THEN
                         CREATE ROLE {RlsProbeRole} LOGIN NOSUPERUSER NOBYPASSRLS;
                     END IF;
                 END $$;
                 GRANT USAGE ON SCHEMA identity TO {RlsProbeRole};
                 GRANT SELECT ON ALL TABLES IN SCHEMA identity TO {RlsProbeRole};
                 """,
                admin);

            await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        builder.Username = RlsProbeRole;

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.current_company_id', $1, false)", connection))
        {
            scope.Parameters.Add(new NpgsqlParameter { Value = company.Value.ToString() });
            await scope.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(query, connection);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task DropProbeRoleAsync()
    {
        if (_database is null)
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_database);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"""
                 REVOKE ALL ON ALL TABLES IN SCHEMA identity FROM {RlsProbeRole};
                 REVOKE ALL ON SCHEMA identity FROM {RlsProbeRole};
                 DROP ROLE IF EXISTS {RlsProbeRole};
                 """,
                connection);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // A scratch role outliving a failed run is untidy, not unsafe.
        }
    }
}

/// <summary>Covers the deployment defaults' startup validation.</summary>
/// <remarks>
/// Needs no server: the point is that a deployment whose default policy no Company could save
/// refuses to start, and that decision is made from configuration alone.
/// </remarks>
public sealed class AuthenticationPolicyDefaultsTests
{
    [Fact]
    public void TheShippedDefaultsAreValid()
    {
        Assert.True(Validate(new AuthenticationPolicyDefaults()).Succeeded);
    }

    [Fact]
    public void ADefaultBelowThePlatformFloorIsRefused()
    {
        // The one that matters: a deployment could otherwise set every Company's floor below the
        // platform's, and no Company would ever see the setting that did it.
        var result = Validate(new AuthenticationPolicyDefaults { MinimumPasswordLength = 4 });

        Assert.True(result.Failed);
        Assert.Contains("could save", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultWithAnAbsoluteLifetimeShorterThanTheIdleWindowIsRefused()
    {
        // The relational rule, which the field ranges cannot express — which is why the validator
        // asks the aggregate rather than re-deriving the bounds.
        var result = Validate(new AuthenticationPolicyDefaults
        {
            IdleTimeoutMinutes = 600,
            AbsoluteLifetimeMinutes = 60
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(100)]
    public void ADefaultLockoutThresholdOutsideItsBoundsIsRefused(int attempts)
    {
        Assert.True(
            Validate(new AuthenticationPolicyDefaults { MaximumFailedAttempts = attempts }).Failed);
    }

    [Fact]
    public void TheDefaultsMatchTheAggregatesOwnFallback()
    {
        // Two sets of defaults that drifted would make behaviour depend on whether a Company had
        // ever opened the settings page — configured-to-the-defaults and never-configured would
        // stop being the same thing.
        var options = new AuthenticationPolicyDefaults();
        var fallback = CompanyAuthenticationPolicy.Default(new CompanyId(Guid.CreateVersion7()));

        Assert.Equal(fallback.MinimumPasswordLength, options.MinimumPasswordLength);
        Assert.Equal(fallback.RequireBreachCheck, options.RequireBreachCheck);
        Assert.Equal(fallback.IdleTimeoutMinutes, options.IdleTimeoutMinutes);
        Assert.Equal(fallback.AbsoluteLifetimeMinutes, options.AbsoluteLifetimeMinutes);
        Assert.Equal(fallback.MfaRequired, options.MfaRequired);
        Assert.Equal(fallback.MaximumFailedAttempts, options.MaximumFailedAttempts);
        Assert.Equal(fallback.LockoutMinutes, options.LockoutMinutes);
    }

    private static ValidateOptionsResult Validate(AuthenticationPolicyDefaults options) =>
        new AuthenticationPolicyDefaultsValidator().Validate(null, options);
}
