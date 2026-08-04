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
/// Drives role assignment through the real HTTP pipeline.
/// </summary>
/// <remarks>
/// <b>Redis is configured here, because this is the milestone that finally calls
/// <c>InvalidateAsync</c>.</b> Running these against a disabled cache would exercise the
/// assignment path and skip the only part of it that is new — and the failure it guards against is
/// silent: a role removed but still cached is a permission still in force.
/// <para>
/// Two Companies and three Employees: an administrator who can manage, a reader who can only read,
/// and a subject in another Company. Skipped when either PostgreSQL or Redis is missing.
/// </para>
/// </remarks>
public sealed class EmployeeRoleEndpointTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private const string AdminRole = "company-admin";
    private const string ReaderRole = "billing-admin";
    private const string LeadRole = "team-lead";

    /// <summary>A login role that cannot bypass row-level security.</summary>
    private const string RlsProbeRole = "maintorbit_roles_probe";

    private readonly CompanyId _companyA = new(Guid.CreateVersion7());
    private readonly CompanyId _companyB = new(Guid.CreateVersion7());
    private readonly string _prefix = TestRedis.NewKeyPrefix();

    private IHost? _host;
    private string? _skip;
    private string? _database;

    private EmployeeId _adminId;
    private EmployeeId _subjectId;
    private EmployeeId _foreignId;
    private string _adminToken = string.Empty;
    private string _subjectToken = string.Empty;

    public async Task InitializeAsync()
    {
        if (!TestRedis.IsAvailable)
        {
            _skip = "No Redis reachable.";
            return;
        }

        _database = await TestDatabase.CreateAsync().ConfigureAwait(false);

        if (_database is null)
        {
            _skip = "No PostgreSQL reachable.";
            return;
        }

        _host = BuildHost(_database);
        _host.Start();

        await MigrateAndSeedCatalogueAsync().ConfigureAwait(false);

        _adminId = await SeedEmployeeAsync(_companyA, "ada@a.test").ConfigureAwait(false);
        _subjectId = await SeedEmployeeAsync(_companyA, "sam@a.test").ConfigureAwait(false);
        _foreignId = await SeedEmployeeAsync(_companyB, "bea@b.test").ConfigureAwait(false);

        // The administrator can manage and read; the subject can only read. Both are real
        // assignments through the same table the endpoints write.
        await SeedGrantAsync(AdminRole, IdentityPermissions.EmployeeManage.Value).ConfigureAwait(false);
        await SeedGrantAsync(AdminRole, IdentityPermissions.EmployeeRead.Value).ConfigureAwait(false);
        await SeedGrantAsync(ReaderRole, IdentityPermissions.EmployeeRead.Value).ConfigureAwait(false);

        await SeedAssignmentAsync(_companyA, _adminId, AdminRole).ConfigureAwait(false);
        await SeedAssignmentAsync(_companyA, _subjectId, ReaderRole).ConfigureAwait(false);

        _adminToken = await SignInAsync("ada@a.test").ConfigureAwait(false);
        _subjectToken = await SignInAsync("sam@a.test").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestRedis.DropAsync(_prefix).ConfigureAwait(false);

        // The probe role is cluster-wide, so dropping the database does not remove it.
        await DropProbeRoleAsync().ConfigureAwait(false);
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

                            // A long lifetime on purpose: a stale entry must not expire on its own
                            // during a test, so anything that becomes effective did so because
                            // something invalidated it.
                            ["PermissionCache:ConnectionString"] = TestRedis.ConnectionString,
                            ["PermissionCache:TimeToLiveSeconds"] = "59",
                            ["PermissionCache:KeyPrefix"] = _prefix
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
                        endpoints.MapEmployeeRoleEndpoints();
                    });
                }))
            .Build();

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DependencyAvailability_IsReported()
    {
        // Makes the skip visible instead of silent.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Assigning ------------------------------------------------------------------------------

    [Fact]
    public async Task AssigningARoleReturnsCreatedWithItsLocation()
    {
        if (Unavailable()) { return; }

        var (status, body, location) = await AssignAsync(_subjectId, LeadRole, "Company");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(LeadRole, body.GetProperty("roleCode").GetString());
        Assert.Equal("Company", body.GetProperty("scope").GetString());

        var id = body.GetProperty("id").GetGuid();

        // §4.1: create returns 201 with a Location addressing what was made. The assignment is the
        // handle removal takes, so the location has to name it rather than the role.
        Assert.NotNull(location);
        Assert.EndsWith($"/employees/{_subjectId.Value}/roles/{id}", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssignedRoleAppearsInTheList()
    {
        if (Unavailable()) { return; }

        await AssignAsync(_subjectId, LeadRole, "Company");

        var (status, body) = await GetAsync($"/api/v1/employees/{_subjectId.Value}/roles", _adminToken);

        Assert.Equal(HttpStatusCode.OK, status);

        // The reader role it was seeded with, plus the one just granted.
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.Contains(
            body.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("roleCode").GetString() == LeadRole);
    }

    [Fact]
    public async Task ATeamScopedAssignmentCarriesItsTeam()
    {
        if (Unavailable()) { return; }

        var team = Guid.CreateVersion7();

        var (status, body, _) = await AssignAsync(_subjectId, LeadRole, "Team", team);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Team", body.GetProperty("scope").GetString());
        Assert.Equal(team, body.GetProperty("scopeId").GetGuid());
    }

    [Fact]
    public async Task TheSameRoleAtTwoTeamsIsTwoAssignments()
    {
        if (Unavailable()) { return; }

        // Which is why removal addresses an assignment rather than a role: the pair (role, scope)
        // is not a handle a client could use here.
        await AssignAsync(_subjectId, LeadRole, "Team", Guid.CreateVersion7());
        var second = await AssignAsync(_subjectId, LeadRole, "Team", Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Created, second.Status);

        var (_, body) = await GetAsync($"/api/v1/employees/{_subjectId.Value}/roles", _adminToken);

        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
    }

    // ---- Duplicate prevention ---------------------------------------------------------------------

    [Fact]
    public async Task AssigningTheSameRoleAtTheSameScopeTwiceIsAConflict()
    {
        if (Unavailable()) { return; }

        Assert.Equal(HttpStatusCode.Created, (await AssignAsync(_subjectId, LeadRole, "Company")).Status);

        var (status, body, _) = await AssignAsync(_subjectId, LeadRole, "Company");

        // §7 maps this to 409. The unique index enforces it regardless; checking first turns a
        // constraint violation into an answer the caller can act on.
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheSameRoleAtTheSameTeamTwiceIsAConflict()
    {
        if (Unavailable()) { return; }

        var team = Guid.CreateVersion7();

        Assert.Equal(
            HttpStatusCode.Created, (await AssignAsync(_subjectId, LeadRole, "Team", team)).Status);
        Assert.Equal(
            HttpStatusCode.Conflict, (await AssignAsync(_subjectId, LeadRole, "Team", team)).Status);
    }

    [Fact]
    public async Task TheDatabaseRefusesADuplicateEvenWithoutTheCheck()
    {
        if (Unavailable()) { return; }

        // The check is a courtesy; this is the guarantee. Two concurrent requests can both pass
        // the existence check, and only the constraint decides.
        //
        // It became a real guarantee in this milestone. The index was created without NULLS NOT
        // DISTINCT, and scope_id is NULL for every Company- and Self-scoped assignment — so it
        // prevented duplicates only at Team scope, the rarest of the three.
        await AssignAsync(_subjectId, LeadRole, "Company");

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_companyA);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.EmployeeRoles.Add(EmployeeRole.Assign(
            _companyA, _subjectId, RoleCode.Create(LeadRole),
            PermissionScope.Company, null, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // ---- Validation -------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingFieldIsAValidationFailure()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync(
            $"/api/v1/employees/{_subjectId.Value}/roles",
            new { roleCode = "", scope = "" },
            _adminToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task AnUnknownScopeIsRefused()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync(
            $"/api/v1/employees/{_subjectId.Value}/roles",
            new { roleCode = LeadRole, scope = "Galaxy" },
            _adminToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ATeamScopeWithoutATeamIsRefused()
    {
        if (Unavailable()) { return; }

        // §3.5: a Team-scoped assignment with no Team reaches nothing. Accepting it would store
        // configuration that reads as a grant and behaves as none.
        var (status, _, _) = await AssignAsync(_subjectId, LeadRole, "Team", scopeId: null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task ANonTeamScopeWithATeamIsRefused()
    {
        if (Unavailable()) { return; }

        // The other half: a Company-scoped assignment naming a Team implies a limit that is not
        // enforced.
        foreach (var scope in new[] { "Company", "Self" })
        {
            var (status, _, _) = await AssignAsync(_subjectId, LeadRole, scope, Guid.CreateVersion7());

            Assert.Equal(HttpStatusCode.BadRequest, status);
        }
    }

    [Fact]
    public async Task AnUndefinedRoleIsRefused()
    {
        if (Unavailable()) { return; }

        // The foreign key would refuse it too; what this asserts is that the caller gets a
        // not_found rather than a 500 out of a constraint violation.
        var (status, body, _) = await AssignAsync(_subjectId, "not-a-real-role", "Company");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnUnknownEmployeeIsRefused()
    {
        if (Unavailable()) { return; }

        var (status, _, _) = await AssignAsync(new EmployeeId(Guid.CreateVersion7()), LeadRole, "Company");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- Removal ------------------------------------------------------------------------------------

    [Fact]
    public async Task RemovingAnAssignmentReturnsNoContentAndDropsItFromTheList()
    {
        if (Unavailable()) { return; }

        var (_, created, _) = await AssignAsync(_subjectId, LeadRole, "Company");
        var id = created.GetProperty("id").GetGuid();

        var status = await DeleteAsync($"/api/v1/employees/{_subjectId.Value}/roles/{id}", _adminToken);

        Assert.Equal(HttpStatusCode.NoContent, status);

        var (_, list) = await GetAsync($"/api/v1/employees/{_subjectId.Value}/roles", _adminToken);

        Assert.DoesNotContain(
            list.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotThereIsNotFound()
    {
        if (Unavailable()) { return; }

        var status = await DeleteAsync(
            $"/api/v1/employees/{_subjectId.Value}/roles/{Guid.CreateVersion7()}", _adminToken);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task RemovingTwiceIsNotFoundTheSecondTime()
    {
        if (Unavailable()) { return; }

        var (_, created, _) = await AssignAsync(_subjectId, LeadRole, "Company");
        var path = $"/api/v1/employees/{_subjectId.Value}/roles/{created.GetProperty("id").GetGuid()}";

        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(path, _adminToken));
        Assert.Equal(HttpStatusCode.NotFound, await DeleteAsync(path, _adminToken));
    }

    [Fact]
    public async Task AnAssignmentCannotBeRemovedThroughAnotherEmployeesUrl()
    {
        if (Unavailable()) { return; }

        // Without checking the Employee, a caller holding an identifier could remove an assignment
        // through any URL, and the path would describe something other than what happened.
        var (_, created, _) = await AssignAsync(_subjectId, LeadRole, "Company");
        var id = created.GetProperty("id").GetGuid();

        var status = await DeleteAsync(
            $"/api/v1/employees/{_adminId.Value}/roles/{id}", _adminToken);

        Assert.Equal(HttpStatusCode.NotFound, status);

        // And it is still there.
        var (_, list) = await GetAsync($"/api/v1/employees/{_subjectId.Value}/roles", _adminToken);

        Assert.Contains(
            list.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == id);
    }

    // ---- Cache invalidation ---------------------------------------------------------------------------

    [Fact]
    public async Task AssigningTakesEffectOnTheNextRequest()
    {
        if (Unavailable()) { return; }

        // The subject can read but not manage, and their permissions are now cached with a
        // 59-second lifetime. Without invalidation the new grant would wait that out.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await AssignAsync(_foreignId, LeadRole, "Company", token: _subjectToken)).Status);

        await AssignAsync(_subjectId, AdminRole, "Company");

        // Same token, same cached entry — except it is not cached any more.
        var (status, _, _) = await AssignAsync(_subjectId, LeadRole, "Company", token: _subjectToken);

        Assert.Equal(HttpStatusCode.Created, status);
    }

    [Fact]
    public async Task RemovingTakesEffectOnTheNextRequest()
    {
        if (Unavailable()) { return; }

        // The direction that matters most. A removed assignment still cached is a permission still
        // in force after it was taken away.
        var (_, created, _) = await AssignAsync(_subjectId, AdminRole, "Company");

        Assert.Equal(
            HttpStatusCode.Created,
            (await AssignAsync(_subjectId, LeadRole, "Company", token: _subjectToken)).Status);

        await DeleteAsync(
            $"/api/v1/employees/{_subjectId.Value}/roles/{created.GetProperty("id").GetGuid()}",
            _adminToken);

        var (status, _, _) = await AssignAsync(_foreignId, LeadRole, "Company", token: _subjectToken);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task OnlyTheAffectedEmployeesEntryIsDropped()
    {
        if (Unavailable()) { return; }

        // Invalidating too widely is not a security fault, but it is a cache that stops being one
        // the first time an administrator does a round of assignments.
        await GetAsync("/api/v1/employees", _adminToken);

        using var connection = TestRedis.Connect();
        var adminKey = $"{_prefix}:{_companyA.Value:n}:{_adminId.Value:n}";

        Assert.True(await connection.GetDatabase().KeyExistsAsync(adminKey));

        await AssignAsync(_subjectId, LeadRole, "Company");

        Assert.True(await connection.GetDatabase().KeyExistsAsync(adminKey));
        Assert.False(await connection.GetDatabase().KeyExistsAsync(
            $"{_prefix}:{_companyA.Value:n}:{_subjectId.Value:n}"));
    }

    // ---- Authorization ------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingRequiresOnlyTheReadPermission()
    {
        if (Unavailable()) { return; }

        // The subject holds employee.read and not employee.manage.
        var (status, _) = await GetAsync(
            $"/api/v1/employees/{_adminId.Value}/roles", _subjectToken);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task ChangingRequiresTheManagePermission()
    {
        if (Unavailable()) { return; }

        // Deciding what another Employee may do is strictly larger than seeing what they may do,
        // so read is not enough.
        var assign = await AssignAsync(_adminId, LeadRole, "Company", token: _subjectToken);

        Assert.Equal(HttpStatusCode.Forbidden, assign.Status);
        Assert.Equal("permission_denied", assign.Body.GetProperty("type").GetString());

        var remove = await DeleteAsync(
            $"/api/v1/employees/{_adminId.Value}/roles/{Guid.CreateVersion7()}", _subjectToken);

        Assert.Equal(HttpStatusCode.Forbidden, remove);
    }

    [Fact]
    public async Task EveryEndpointRequiresAuthentication()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        var basePath = $"/api/v1/employees/{_subjectId.Value}/roles";

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(basePath, null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostAsync(basePath, new { roleCode = LeadRole, scope = "Company" }, null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await DeleteAsync($"{basePath}/{Guid.CreateVersion7()}", null));
    }

    [Fact]
    public async Task AResponseRunsThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/employees/{_subjectId.Value}/roles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    // ---- Tenant isolation ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmployeeInAnotherCompanyIsInvisibleToTheLookup()
    {
        if (Unavailable()) { return; }

        // Asserted against a NOSUPERUSER NOBYPASSRLS role, because the developer account these
        // tests connect as is a superuser and PostgreSQL exempts a superuser from every policy.
        // Driving this through the endpoint would return 201 here whether the policy existed or
        // not — the single most misleading green test this class could contain.
        //
        // What the handler does with an invisible Employee is settled by AnUnknownEmployeeIsRefused:
        // FindAsync returns null and the answer is 404, which §7 requires for a cross-tenant
        // reference — "never 403", because a forbidden answer confirms the Employee exists.
        const string Query =
            "SELECT count(*) FROM identity.employees WHERE deleted_at_utc IS NULL";

        Assert.Equal(2, await AsRestrictedRoleAsync(_companyA, Query));
        Assert.Equal(1, await AsRestrictedRoleAsync(_companyB, Query));

        // And the foreign Employee's row is not among the Company the caller is scoped to.
        Assert.Equal(0, await AsRestrictedRoleAsync(
            _companyA,
            $"SELECT count(*) FROM identity.employees WHERE id = '{_foreignId.Value}'"));
    }

    [Fact]
    public async Task AssignmentsAreInvisibleAcrossCompanies()
    {
        if (Unavailable()) { return; }

        // The removal path finds an assignment by identifier alone, so the policy on
        // employee_roles is the only thing stopping a caller from removing another Company's.
        await AssignAsync(_subjectId, LeadRole, "Company");

        const string Query = "SELECT count(*) FROM identity.employee_roles";

        // Two seeded plus the one just made, and nothing in the other Company.
        Assert.Equal(3, await AsRestrictedRoleAsync(_companyA, Query));
        Assert.Equal(0, await AsRestrictedRoleAsync(_companyB, Query));
    }

    [Fact]
    public async Task AnAssignmentIsWrittenUnderTheCallersCompany()
    {
        if (Unavailable()) { return; }

        // TC-1: the tenant comes from the credential, and there is no field that could name
        // another. Verified against the stored row rather than the response.
        var (_, created, _) = await AssignAsync(_subjectId, LeadRole, "Company");
        var id = created.GetProperty("id").GetGuid();

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_companyA);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var stored = await context.EmployeeRoles.SingleAsync(assignment => assignment.Id == id);

        Assert.Equal(_companyA, stored.CompanyId);
        Assert.Equal(_adminId, stored.CreatedByEmployeeId);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private Task<(HttpStatusCode Status, JsonElement Body, string? Location)> AssignAsync(
        EmployeeId employee, string role, string scope, Guid? scopeId = null, string? token = null) =>
        PostWithLocationAsync(
            $"/api/v1/employees/{employee.Value}/roles",
            new { roleCode = role, scope, scopeId },
            token ?? _adminToken);

    private async Task<(HttpStatusCode Status, JsonElement Body, string? Location)>
        PostWithLocationAsync(string path, object payload, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone(),
            response.Headers.Location?.ToString());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path, object payload, string? bearer)
    {
        var (status, body, _) = await PostWithLocationAsync(path, payload, bearer)
            .ConfigureAwait(false);

        return (status, body);
    }

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
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<HttpStatusCode> DeleteAsync(string path, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        return response.StatusCode;
    }

    /// <summary>
    /// Runs a scalar query as a role that cannot bypass row-level security.
    /// </summary>
    /// <remarks>
    /// The application's own connection cannot be used for this: it authenticates as the developer
    /// account, which is a superuser and therefore exempt from every policy, so a query on it
    /// observes the data rather than the isolation.
    /// </remarks>
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
            // The role was never created, or another class's database still grants to it. A
            // scratch role outliving a failed run is untidy, not unsafe.
        }
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

    private async Task MigrateAndSeedCatalogueAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.Database.MigrateAsync().ConfigureAwait(false);

        context.Permissions.AddRange(
            Permission.Define(IdentityPermissions.EmployeeRead, "Read Employees"),
            Permission.Define(IdentityPermissions.EmployeeManage, "Manage Employees"));

        context.RoleDefinitions.AddRange(
            RoleDefinition.Define(RoleCode.Create(AdminRole), "Company Admin", isBuiltIn: true),
            RoleDefinition.Define(RoleCode.Create(ReaderRole), "Billing Admin", isBuiltIn: true),
            RoleDefinition.Define(RoleCode.Create(LeadRole), "Team Lead", isBuiltIn: true));

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

    private async Task SeedGrantAsync(string role, string permission)
    {
        using var scope = _host!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.RolePermissions.Add(
            RolePermission.Grant(RoleCode.Create(role), PermissionCode.Create(permission)));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedAssignmentAsync(CompanyId company, EmployeeId employee, string role)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        context.EmployeeRoles.Add(EmployeeRole.Assign(
            company, employee, RoleCode.Create(role),
            PermissionScope.Company, scopeId: null, DateTimeOffset.UtcNow));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
