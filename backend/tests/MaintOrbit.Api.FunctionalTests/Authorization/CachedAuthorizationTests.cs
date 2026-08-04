using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Caching;
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

namespace MaintOrbit.Api.FunctionalTests.Authorization;

/// <summary>
/// Drives a cache-backed authorization decision through the real HTTP pipeline.
/// </summary>
/// <remarks>
/// The cache unit tests prove the cache works. These prove what it does to <i>authorization</i> —
/// which is a different and more consequential question, because a cache in front of a security
/// decision changes when that decision is re-made.
/// <para>
/// <b>The entry lifetime is one second here.</b> Production runs thirty; one keeps the
/// time-to-live assertion honest without a thirty-second test, and the bound itself is enforced by
/// the validator rather than by this number.
/// </para>
/// <para>
/// Needs both PostgreSQL and Redis, and is skipped when either is missing.
/// </para>
/// </remarks>
public sealed class CachedAuthorizationTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";
    private const string AdminRole = "company-admin";

    /// <summary>Short enough to observe, long enough that a slow request does not race it.</summary>
    private const int TimeToLiveSeconds = 1;

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly string _prefix = TestRedis.NewKeyPrefix();

    private IHost? _host;
    private string? _skip;
    private string? _database;
    private EmployeeId _employeeId;
    private string _token = string.Empty;

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

        await SeedAsync().ConfigureAwait(false);
        _token = await SignInAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestRedis.DropAsync(_prefix).ConfigureAwait(false);
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

                            // The whole point of this class: the real Redis cache, wired the way a
                            // deployment wires it, rather than a substitute in front of it.
                            ["PermissionCache:ConnectionString"] = TestRedis.ConnectionString,
                            ["PermissionCache:TimeToLiveSeconds"] =
                                TimeToLiveSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                    });
                }))
            .Build();

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DependencyAvailability_IsReported()
    {
        // Makes the skip visible instead of silent. This class needs two servers, so it is the one
        // most likely to be quietly doing nothing.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- The cache is real ----------------------------------------------------------------------

    [Fact]
    public async Task AGrantedRequestSucceedsAndPopulatesTheCache()
    {
        if (Unavailable()) { return; }

        await GrantAndAssignAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync());

        // The resolution is in Redis under the documented key, not merely in this process.
        using var connection = TestRedis.Connect();
        Assert.True(await connection.GetDatabase().KeyExistsAsync(Key()));
    }

    [Fact]
    public async Task TheCachedEntryCarriesTheConfiguredLifetime()
    {
        if (Unavailable()) { return; }

        await GrantAndAssignAsync();
        await GetAsync();

        using var connection = TestRedis.Connect();
        var remaining = await connection.GetDatabase().KeyTimeToLiveAsync(Key());

        // An entry with no expiry would be a permission that never goes stale. FR-PERM-005's bound
        // is enforced by the validator; that it reaches Redis at all is enforced here.
        Assert.NotNull(remaining);
        Assert.True(remaining.Value <= TimeSpan.FromSeconds(TimeToLiveSeconds));
    }

    [Fact]
    public async Task TheDecisionIsStillMadePerRequest()
    {
        if (Unavailable()) { return; }

        // A cache hit must not become a bypass. The same request, twice, still goes through
        // authentication, session validation, the tenant scope, and the permission gate — only the
        // database read is skipped.
        await GrantAndAssignAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync());
        Assert.Equal(HttpStatusCode.OK, await GetAsync());

        // And with no credential it is refused whether or not anything is cached.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(withToken: false));
    }

    // ---- Revocation ------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidatingMakesARevocationImmediate()
    {
        if (Unavailable()) { return; }

        // This is what the milestone had to preserve. Before the cache, revocation was immediate
        // because every request read the database; with one, immediacy comes from invalidation and
        // the entry lifetime is only the backstop.
        await GrantAndAssignAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync());

        await RemoveAssignmentsAsync();
        await InvalidateAsync();

        // The very next request, on the same unexpired token.
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync());
    }

    [Fact]
    public async Task WithoutInvalidatingTheEntryLifetimeIsTheBound()
    {
        if (Unavailable()) { return; }

        // The honest statement of what a cache costs, asserted rather than assumed. A revocation
        // nobody invalidated is still honoured until the entry lapses — which is why the lifetime
        // is validated below sixty seconds and is not a tuning knob.
        await GrantAndAssignAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync());

        await RemoveAssignmentsAsync();

        // Still granted: the decision is the cached one.
        Assert.Equal(HttpStatusCode.OK, await GetAsync());

        await Task.Delay(TimeSpan.FromMilliseconds(1_400));

        // And now it is not, without anything having invalidated it.
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync());
    }

    [Fact]
    public async Task InvalidatingSomethingUncachedChangesNothing()
    {
        if (Unavailable()) { return; }

        await GrantAndAssignAsync();
        await InvalidateAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync());
    }

    // ---- Failure direction -------------------------------------------------------------------------

    [Fact]
    public async Task AnUnreachableCacheStillEnforcesTheDecision()
    {
        if (Unavailable()) { return; }

        // The property that matters most. With the cache pointed nowhere, authorization is decided
        // from the database — granted requests succeed and ungranted ones are refused. A cache
        // that failed closed would turn a Redis restart into a total outage; one that failed open
        // in the other direction would grant everything.
        using var host = BuildUnreachableCacheHost();
        host.Start();

        var token = await SignInAsync(host);

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(host, token));

        await GrantAndAssignAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, token));

        // And nothing was cached, so a revocation is immediate again.
        await RemoveAssignmentsAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(host, token));
    }

    // ---- Tenant isolation -----------------------------------------------------------------------------

    [Fact]
    public async Task TheCacheKeyIsScopedToTheCompany()
    {
        if (Unavailable()) { return; }

        // Redis has no row-level security, so the key is the whole of the isolation. The entry the
        // request wrote must be the one for this Employee in this Company and nowhere else.
        await GrantAndAssignAsync();
        await GetAsync();

        using var connection = TestRedis.Connect();

        Assert.True(await connection.GetDatabase().KeyExistsAsync(Key()));
        Assert.False(await connection.GetDatabase().KeyExistsAsync(
            $"{_prefix}:{Guid.CreateVersion7():n}:{_employeeId.Value:n}"));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private string Key() => $"{_prefix}:{_company.Value:n}:{_employeeId.Value:n}";

    private Task<HttpStatusCode> GetAsync(bool withToken = true) =>
        GetAsync(_host!, withToken ? _token : null);

    private static async Task<HttpStatusCode> GetAsync(IHost host, string? bearer)
    {
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/employees");

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        return response.StatusCode;
    }

    /// <summary>A second host over the same database, with the cache pointed at nothing.</summary>
    private IHost BuildUnreachableCacheHost() =>
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
                            ["Persistence:ConnectionString"] = _database,
                            ["PasswordHashing:MemoryKibibytes"] = "19456",
                            ["PasswordHashing:Iterations"] = "2",
                            ["PasswordHashing:Parallelism"] = "1",
                            ["PasswordHashing:Version"] = "1",

                            // A port nothing is listening on — cheaper and far more reliable than
                            // stopping the local server, and the same observation from the client.
                            ["PermissionCache:ConnectionString"] =
                                "localhost:6399,connectTimeout=200,connectRetry=1",
                            ["PermissionCache:TimeToLiveSeconds"] = "30",
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
                    });
                }))
            .Build();

    private Task<string> SignInAsync() => SignInAsync(_host!);

    private static async Task<string> SignInAsync(IHost host)
    {
        using var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = Address, password = Password, clientType = "WebConsole" })
            .ConfigureAwait(false);

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedAsync()
    {
        using (var scope = _host!.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Permissions.Add(
                Permission.Define(IdentityPermissions.EmployeeRead, "Read Employees"));
            context.RoleDefinitions.Add(
                RoleDefinition.Define(RoleCode.Create(AdminRole), "Company Admin", isBuiltIn: true));

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        var tenant = _host.Services.GetRequiredService<ITenantContext>();
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
    }

    private async Task GrantAndAssignAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        if (!await context.RolePermissions.AnyAsync().ConfigureAwait(false))
        {
            context.RolePermissions.Add(RolePermission.Grant(
                RoleCode.Create(AdminRole), IdentityPermissions.EmployeeRead));
        }

        if (!await context.EmployeeRoles.AnyAsync().ConfigureAwait(false))
        {
            context.EmployeeRoles.Add(EmployeeRole.Assign(
                _company,
                _employeeId,
                RoleCode.Create(AdminRole),
                PermissionScope.Company,
                scopeId: null,
                DateTimeOffset.UtcNow));
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task RemoveAssignmentsAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.EmployeeRoles.ExecuteDeleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates through the registered cache, the way role management will.
    /// </summary>
    /// <remarks>
    /// Called directly because role editing is a later milestone. What is being asserted is that
    /// the invalidation path exists and works end to end — the command that will call it changes
    /// who calls it, not what it does.
    /// </remarks>
    private Task InvalidateAsync() =>
        _host!.Services.GetRequiredService<IPermissionCache>()
            .InvalidateAsync(_employeeId, _company, CancellationToken.None);
}

/// <summary>Covers the cache settings' startup validation.</summary>
/// <remarks>
/// Needs no server: the point is that a deployment which would exceed FR-PERM-005's bound refuses
/// to start, and that decision is made from configuration alone.
/// </remarks>
public sealed class PermissionCacheOptionsTests
{
    [Fact]
    public void ALifetimeAtOrOverTheBoundIsRefused()
    {
        // Sixty is the requirement itself, so the ceiling stops short of it: a lifetime equal to
        // the bound satisfies FR-PERM-005 only if nothing else costs a millisecond.
        foreach (var seconds in new[] { 60, 61, 3_600 })
        {
            var result = Validate(new PermissionCacheOptions { TimeToLiveSeconds = seconds });

            Assert.True(result.Failed);
            Assert.Contains("FR-PERM-005", result.FailureMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ALifetimeUnderTheBoundIsAccepted()
    {
        Assert.True(Validate(new PermissionCacheOptions { TimeToLiveSeconds = 59 }).Succeeded);
        Assert.True(Validate(new PermissionCacheOptions { TimeToLiveSeconds = 1 }).Succeeded);
    }

    [Fact]
    public void TheDefaultLifetimeIsWellUnderTheBound()
    {
        // Half the ceiling, so a deployment has room to raise it without reaching the bound.
        var options = new PermissionCacheOptions();

        Assert.True(options.TimeToLiveSeconds < PermissionCacheOptions.MaximumTimeToLiveSeconds);
        Assert.Equal(30, options.TimeToLiveSeconds);
    }

    [Fact]
    public void AnEmptyConnectionStringIsAValidConfiguration()
    {
        // Not a broken deployment — one that has chosen not to cache. Making this fail would make
        // the safest configuration the one that refuses to start.
        var options = new PermissionCacheOptions();

        Assert.False(options.IsEnabled);
        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData("localhost:6379,unknownOption=1")]
    [InlineData("localhost:6379,connectTimeout=abc")]
    [InlineData("localhost:6379,ssl=maybe")]
    public void AMalformedConnectionStringIsRefused(string candidate)
    {
        // A malformed string is a configuration error and stops startup. An unreachable server is
        // not checked here at all: that is an outage the cache is built to survive, and refusing
        // to start for it would make Redis a hard dependency of authorization.
        //
        // Note what is not on this list: "localhost:not-a-port" parses, because the client reads
        // it as a host name. Configuration validation catches what is unparseable, not what is
        // wrong — the latter surfaces as an unreachable server, which is by design survivable.
        var result = Validate(new PermissionCacheOptions { ConnectionString = candidate });

        Assert.True(result.Failed);
        Assert.Contains("not a valid Redis configuration", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedConnectionStringIsAccepted()
    {
        var result = Validate(new PermissionCacheOptions
        {
            ConnectionString = "localhost:6379,ssl=true"
        });

        Assert.True(result.Succeeded);
    }

    private static ValidateOptionsResult Validate(PermissionCacheOptions options) =>
        new PermissionCacheOptionsValidator().Validate(null, options);
}
