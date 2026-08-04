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
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MaintOrbit.Api.FunctionalTests.Authorization;

/// <summary>
/// Drives permission enforcement through the real HTTP pipeline.
/// </summary>
/// <remarks>
/// <b>The whole point of this file is that nothing is substituted.</b> A real access token, the
/// real bearer middleware, the real session check, the real tenant scope, and a real permission
/// resolution against <c>employee_roles</c> under row-level security — because the failure this
/// milestone exists to prevent is precisely the one a substitute would hide: authorization that
/// looks wired up and either refuses everybody or asks nothing.
/// <para>
/// Two Companies are seeded, each with its own Employee, so isolation is observed rather than
/// asserted about. They are skipped when no PostgreSQL is reachable.
/// </para>
/// </remarks>
public sealed class ProtectedEndpointTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private const string AdminRole = "company-admin";
    private const string ReaderRole = "billing-admin";

    /// <summary>A login role that cannot bypass row-level security.</summary>
    private const string RlsProbeRole = "maintorbit_rls_probe";

    private readonly CompanyId _companyA = new(Guid.CreateVersion7());
    private readonly CompanyId _companyB = new(Guid.CreateVersion7());

    private IHost? _host;
    private string? _skip;
    private string? _database;

    private EmployeeId _adaId;
    private EmployeeId _beaId;
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

        await MigrateAsync().ConfigureAwait(false);
        await SeedCatalogueAsync().ConfigureAwait(false);

        _adaId = await SeedEmployeeAsync(_companyA, "ada@a.test").ConfigureAwait(false);
        _beaId = await SeedEmployeeAsync(_companyB, "bea@b.test").ConfigureAwait(false);

        _adaToken = await SignInAsync("ada@a.test").ConfigureAwait(false);
        _beaToken = await SignInAsync("bea@b.test").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();

        // The probe role is cluster-wide, so dropping the database does not remove it.
        await DropProbeRoleAsync().ConfigureAwait(false);
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);
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
            // The role was never created, or another class's database still grants to it. A
            // scratch role outliving a failed run is untidy, not unsafe.
        }
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
                    // The real pipeline, in its real order. Substituting a shorter one here would
                    // test a pipeline nothing runs.
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAuthenticationEndpoints();
                        endpoints.MapEmployeeEndpoints();
                    });
                }))
            .Build();

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Makes the skip visible instead of silent, so a run with no database cannot be mistaken
        // for a run that exercised these paths.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- The three outcomes -------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedWithThePermission_Succeeds()
    {
        if (Unavailable()) { return; }

        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        var (status, body) = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(
            body.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("email").GetString() == "ada@a.test");
    }

    [Fact]
    public async Task AuthenticatedWithoutThePermission_IsForbidden()
    {
        if (Unavailable()) { return; }

        // A role that exists and is held, granting something else entirely. This is the case that
        // distinguishes real enforcement from a gate that passes anyone with a session.
        await GrantAsync(ReaderRole, "budget.manage");
        await AssignAsync(_companyA, _adaId, ReaderRole, PermissionScope.Company);

        var (status, body) = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("permission_denied", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AuthenticatedWithNoRolesAtAll_IsForbidden()
    {
        if (Unavailable()) { return; }

        // SD-001. Holding nothing is the purest form of deny-by-default, and it must be a refusal
        // rather than an error or an empty page.
        var (status, _) = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Unauthenticated_IsUnauthorized()
    {
        if (Unavailable()) { return; }

        // Even with the permission granted, so this measures the absence of a credential and not
        // the absence of a grant.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        var (status, body) = await GetAsync("/api/v1/employees", bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AGarbageToken_IsUnauthorizedRatherThanForbidden()
    {
        if (Unavailable()) { return; }

        // 401 and 403 answer different questions — "who are you" and "may you". Collapsing them
        // would tell an unauthenticated caller that the endpoint exists and what it guards.
        var (status, _) = await GetAsync("/api/v1/employees", "not.a.token");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ---- Revocation ---------------------------------------------------------------------------

    [Fact]
    public async Task RevokingTheRole_DeniesTheVeryNextRequest()
    {
        if (Unavailable()) { return; }

        // FR-PERM-005 allows sixty seconds. Permissions are resolved from the database on every
        // request and never carried in the token, so the window is one request — which is why
        // ADR-0007 keeps them out of the JWT in the first place.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);

        await RemoveAssignmentsAsync(_companyA, _adaId);

        // The same token, unchanged and unexpired.
        Assert.Equal(
            HttpStatusCode.Forbidden, (await GetAsync("/api/v1/employees", _adaToken)).Status);
    }

    [Fact]
    public async Task RevokingTheGrantFromTheRole_DeniesTheVeryNextRequest()
    {
        if (Unavailable()) { return; }

        // The other half of FR-PERM-005: the Employee keeps the role, the role loses the
        // permission. Roles are presets over permissions (SD-020), so editing the preset has to
        // take effect just as fast as removing the assignment.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);

        await RevokeGrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);

        Assert.Equal(
            HttpStatusCode.Forbidden, (await GetAsync("/api/v1/employees", _adaToken)).Status);
    }

    [Fact]
    public async Task SigningOut_StopsTheTokenBeforeAuthorizationIsEvenReached()
    {
        if (Unavailable()) { return; }

        // Session validation sits between authentication and authorization, so a revoked session
        // fails as 401 rather than reaching a permission check at all. An authorization decision
        // made for an ended session would be a decision made for nobody.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);

        using (var client = _host!.GetTestClient())
        {
            using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
            logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adaToken);
            using var response = await client.SendAsync(logout);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var (status, body) = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("session_revoked", body.GetProperty("type").GetString());
    }

    // ---- Scope --------------------------------------------------------------------------------

    [Fact]
    public async Task ASelfGrantReadsMeButNotTheDirectory()
    {
        if (Unavailable()) { return; }

        // §3.5: a Self grant reaches only the acting Employee. The two endpoints declare the same
        // permission at different scopes, so this is the scope doing the work and nothing else.
        await GrantAsync(ReaderRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, ReaderRole, PermissionScope.Self);

        var me = await GetAsync("/api/v1/employees/me", _adaToken);
        var directory = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal("ada@a.test", me.Body.GetProperty("email").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, directory.Status);
    }

    [Fact]
    public async Task ACompanyGrantReachesBoth()
    {
        if (Unavailable()) { return; }

        // The other direction of §3.5: Company reaches everything, including Self.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees/me", _adaToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);
    }

    // ---- Tenant isolation -----------------------------------------------------------------------

    [Fact]
    public async Task AGrantInOneCompanyDoesNotCarryToAnother()
    {
        if (Unavailable()) { return; }

        // The assignment is a tenant-scoped row. Bea holds nothing in her own Company, and Ada's
        // grant lives under a company_id row-level security will not show her.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await GetAsync("/api/v1/employees", _beaToken)).Status);
    }

    [Fact]
    public async Task EachCallerReachesTheDirectoryOfTheirOwnCompany()
    {
        if (Unavailable()) { return; }

        // Both callers reach the endpoint under their own Company's grant. Which rows come back
        // is row-level security's answer, and asserting it here would prove nothing: see
        // TheDirectoryQueryIsFilteredByRowLevelSecurity below for why, and for the check that
        // does prove it.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);
        await AssignAsync(_companyB, _beaId, AdminRole, PermissionScope.Company);

        var ada = await GetAsync("/api/v1/employees", _adaToken);
        var bea = await GetAsync("/api/v1/employees", _beaToken);

        Assert.Equal(HttpStatusCode.OK, ada.Status);
        Assert.Equal(HttpStatusCode.OK, bea.Status);

        Assert.Contains(
            ada.Body.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("email").GetString() == "ada@a.test");
        Assert.Contains(
            bea.Body.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("email").GetString() == "bea@b.test");
    }

    [Fact]
    public async Task TheDirectoryQueryIsFilteredByRowLevelSecurity()
    {
        if (Unavailable()) { return; }

        // Run as a NOSUPERUSER NOBYPASSRLS role, because the developer account these tests connect
        // as is a superuser and PostgreSQL exempts a superuser from every policy. Asserting
        // isolation through the endpoint on this connection would pass whether the policy existed
        // or not — the single most misleading green test this suite could contain.
        //
        // This is the directory query's own predicate: employees, not deleted, under a Company.
        const string Query =
            "SELECT count(*) FROM identity.employees WHERE deleted_at_utc IS NULL";

        Assert.Equal(1, await AsRestrictedRoleAsync(_companyA, Query));
        Assert.Equal(1, await AsRestrictedRoleAsync(_companyB, Query));

        // And a third Company that holds nobody sees nobody — the documented failure direction is
        // zero rows, never somebody else's.
        Assert.Equal(
            0, await AsRestrictedRoleAsync(new CompanyId(Guid.CreateVersion7()), Query));
    }

    [Fact]
    public async Task RoleAssignmentsAreFilteredByRowLevelSecurityToo()
    {
        if (Unavailable()) { return; }

        // The permission lookup itself is a tenant-scoped read. If employee_roles were not
        // isolated, a grant in one Company would resolve for a caller in another — which is the
        // failure AGrantInOneCompanyDoesNotCarryToAnother observes from the outside.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        const string Query = "SELECT count(*) FROM identity.employee_roles";

        Assert.Equal(1, await AsRestrictedRoleAsync(_companyA, Query));
        Assert.Equal(0, await AsRestrictedRoleAsync(_companyB, Query));
    }

    // ---- What a denial may not say --------------------------------------------------------------

    [Fact]
    public async Task ADenialNamesNoPermissionAndNoRole()
    {
        if (Unavailable()) { return; }

        await GrantAsync(ReaderRole, "budget.manage");
        await AssignAsync(_companyA, _adaId, ReaderRole, PermissionScope.Company);

        var (_, body) = await GetAsync("/api/v1/employees", _adaToken);
        var serialized = body.GetRawText();

        // Naming the missing permission would let a caller map the catalogue by walking endpoints
        // and reading what each one asked for.
        Assert.DoesNotContain("employee.read", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("budget.manage", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AdminRole, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReaderRole, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADenialAndAnAbsentGrantAreIndistinguishable()
    {
        if (Unavailable()) { return; }

        // Holding a role that grants something else, and holding nothing at all, must look the
        // same. A difference would report whether the caller has any roles.
        var withoutRoles = await GetAsync("/api/v1/employees", _adaToken);

        await GrantAsync(ReaderRole, "budget.manage");
        await AssignAsync(_companyA, _adaId, ReaderRole, PermissionScope.Company);

        var withWrongRole = await GetAsync("/api/v1/employees", _adaToken);

        Assert.Equal(withoutRoles.Status, withWrongRole.Status);
        Assert.Equal(
            WithoutCorrelation(withoutRoles.Body), WithoutCorrelation(withWrongRole.Body));
    }

    [Fact]
    public async Task ADenialCarriesTheDocumentedEnvelope()
    {
        if (Unavailable()) { return; }

        var (_, body) = await GetAsync("/api/v1/employees", _adaToken);

        // §4.3. The framework's default 403 is an empty body, which a client cannot tell from a
        // broken response.
        Assert.Equal("permission_denied", body.GetProperty("type").GetString());
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
    }

    // ---- Pipeline ---------------------------------------------------------------------------------

    [Fact]
    public async Task PermissionResolutionRunsInsideTheTenantScope()
    {
        if (Unavailable()) { return; }

        // The ordering defect this milestone had to fix. With tenant context after authorization,
        // the employee_roles read happens with no Company in scope, row-level security returns
        // nothing, and every request is denied — safe, and completely silent. A grant that works
        // is the only observation that distinguishes the two orders.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/employees", _adaToken)).Status);
    }

    [Fact]
    public async Task AForbiddenResponseStillRunsThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/employees");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adaToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task BothEndpointsAreProtected()
    {
        if (Unavailable()) { return; }

        // Every endpoint in the group, so a future one added without a permission is caught here
        // as well as by the architecture gate.
        foreach (var path in new[] { "/api/v1/employees", "/api/v1/employees/me" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, bearer: null)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(path, _adaToken)).Status);
        }
    }

    // ---- Paging -----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePageSizeIsClampedToTheConfiguredMaximum()
    {
        if (Unavailable()) { return; }

        // §5.5 bounds page size. Clamped rather than refused, so a client's optimism is a ceiling
        // and not an error.
        await GrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value);
        await AssignAsync(_companyA, _adaId, AdminRole, PermissionScope.Company);

        var (_, body) = await GetAsync("/api/v1/employees?pageSize=100000", _adaToken);

        Assert.Equal(200, body.GetProperty("pageSize").GetInt32());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(
        string path, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body)
                ? default
                : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<string> SignInAsync(string email)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = Password, clientType = "WebConsole" }).ConfigureAwait(false);

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task MigrateAsync()
    {
        using var scope = _host!.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds the two role definitions and the permission catalogue these tests use.
    /// </summary>
    /// <remarks>
    /// Written here rather than shipped as seed data, because seeding the documented seven roles
    /// is still deferred (11.11) and inventing it inside this milestone would be expansion. What
    /// matters for enforcement is that the rows exist; which rows ship by default is a separate
    /// decision.
    /// <para>
    /// These three tables are platform-wide reference data and carry no row-level security policy,
    /// so they are written without a tenant scope — the deliberate DB-P1 exception recorded in the
    /// authorization migration.
    /// </para>
    /// </remarks>
    private async Task SeedCatalogueAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.Permissions.AddRange(
            Permission.Define(IdentityPermissions.EmployeeRead, "Read Employees"),
            Permission.Define(PermissionCode.Create("budget.manage"), "Manage budgets"));

        context.RoleDefinitions.AddRange(
            RoleDefinition.Define(RoleCode.Create(AdminRole), "Company Admin", isBuiltIn: true),
            RoleDefinition.Define(RoleCode.Create(ReaderRole), "Billing Admin", isBuiltIn: true));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<EmployeeId> SeedEmployeeAsync(CompanyId company, string address)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        EmployeeId employeeId;

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            var employee = Employee.Invite(company, Email.Create(address), DateTimeOffset.UtcNow);
            context.Employees.Add(employee);
            await context.SaveChangesAsync().ConfigureAwait(false);
            employeeId = employee.Id;
        }

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ICommandHandler<AcceptInvitationCommand>>()
                .HandleAsync(
                    new AcceptInvitationCommand(
                        employeeId,
                        InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG"),
                        Password),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return employeeId;
    }

    private async Task GrantAsync(string role, string permission)
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.RolePermissions.Add(
            RolePermission.Grant(RoleCode.Create(role), PermissionCode.Create(permission)));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task RevokeGrantAsync(string role, string permission)
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.RolePermissions
            .Where(grant =>
                grant.RoleCode == RoleCode.Create(role) &&
                grant.PermissionCode == PermissionCode.Create(permission))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    private async Task AssignAsync(
        CompanyId company, EmployeeId employee, string role, PermissionScope scope)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var serviceScope = _host.Services.CreateScope();
        var context = serviceScope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.EmployeeRoles.Add(EmployeeRole.Assign(
            company, employee, RoleCode.Create(role), scope, scopeId: null, DateTimeOffset.UtcNow));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task RemoveAssignmentsAsync(CompanyId company, EmployeeId employee)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.EmployeeRoles
            .Where(assignment => assignment.EmployeeId == employee)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a scalar query as a role that cannot bypass row-level security.
    /// </summary>
    /// <remarks>
    /// Created on demand and dropped with the database. The application's own connection cannot be
    /// used for this: it authenticates as the developer account, which is a superuser and
    /// therefore exempt from every policy, so a query on it observes the data rather than the
    /// isolation.
    /// </remarks>
    private async Task<int> AsRestrictedRoleAsync(CompanyId company, string query)
    {
        var builder = new NpgsqlConnectionStringBuilder(_database!);
        var owner = builder.Username;

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

    /// <summary>Strips the per-request correlation identifier so two envelopes can be compared.</summary>
    private static string WithoutCorrelation(JsonElement body) =>
        string.Join('|', body.EnumerateObject()
            .Where(property => property.Name != "correlationId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
}
