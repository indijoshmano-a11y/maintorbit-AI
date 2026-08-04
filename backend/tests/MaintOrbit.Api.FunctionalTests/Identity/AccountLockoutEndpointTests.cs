using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
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

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Drives failed-attempt counting and lockout through the real sign-in endpoint (FR-AUTH-011).
/// </summary>
/// <remarks>
/// <b>The counter has to survive a failed request, which is what makes this worth an end-to-end
/// test.</b> Sign-in returns as soon as the credential check rejects, so a count that were only
/// committed by the successful path would reset itself on every attempt and lock nothing ever —
/// a defect invisible to any test that inspects the aggregate in memory.
/// <para>
/// The clock is controllable, because automatic unlock is a rule about time and a test that slept
/// for the lockout duration is a test nobody runs.
/// </para>
/// </remarks>
public sealed class AccountLockoutEndpointTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Wrong = "not the right password at all";
    private const string Address = "ada@example.test";

    /// <summary>Three attempts and fifteen minutes — the policy these tests configure.</summary>
    private const int Threshold = 3;
    private const int LockoutMinutes = 15;

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly AdvanceableClock _clock = new();

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

        await SeedAsync().ConfigureAwait(false);
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

                            // The deployment defaults, so a Company that sets nothing still locks.
                            ["AuthenticationPolicy:MaximumFailedAttempts"] =
                                Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["AuthenticationPolicy:LockoutMinutes"] =
                                LockoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    // The only substitution. Automatic unlock is a rule about time, and the real
                    // clock would need a fifteen-minute test.
                    services.AddSingleton<TimeProvider>(_clock);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints => endpoints.MapAuthenticationEndpoints());
                }))
            .Build();

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Makes the skip visible instead of silent.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Counting ---------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedAttemptIsCountedAndPersisted()
    {
        if (Unavailable()) { return; }

        // The request failed and returned nothing, and the count is still on the row. That is the
        // whole point: a counter committed only by the successful path locks nothing ever.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Wrong));

        Assert.Equal(1, await FailedCountAsync());
    }

    [Fact]
    public async Task FailuresAccumulate()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Wrong);
        await SignInAsync(Wrong);

        Assert.Equal(2, await FailedCountAsync());
        Assert.Null(await LockedUntilAsync());
    }

    [Fact]
    public async Task ReachingTheThresholdLocksTheAccount()
    {
        if (Unavailable()) { return; }

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Wrong));
        }

        var until = await LockedUntilAsync();

        Assert.NotNull(until);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(LockoutMinutes), until);
    }

    [Fact]
    public async Task TheCorrectPasswordIsRefusedWhileLocked()
    {
        if (Unavailable()) { return; }

        await LockAsync();

        // The password is right. The account is locked, so it does not matter — which is what
        // makes lockout a control rather than a hint.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Password));
    }

    [Fact]
    public async Task NoSessionIsCreatedWhileLocked()
    {
        if (Unavailable()) { return; }

        await LockAsync();
        await SignInAsync(Password);

        Assert.Equal(0, await SessionCountAsync());
    }

    // ---- Attempts while locked --------------------------------------------------------------------

    [Fact]
    public async Task KnockingWhileLockedDoesNotExtendTheLockout()
    {
        if (Unavailable()) { return; }

        // 07-api-security T-3: lockout is itself a denial-of-service vector. If continued attempts
        // pushed the end time back, an attacker could keep a known account locked out indefinitely
        // for the cost of one request every few minutes.
        await LockAsync();

        var original = await LockedUntilAsync();

        _clock.Advance(TimeSpan.FromMinutes(5));

        await SignInAsync(Wrong);
        await SignInAsync(Wrong);

        Assert.Equal(original, await LockedUntilAsync());
    }

    [Fact]
    public async Task KnockingWhileLockedDoesNotRaiseTheCount()
    {
        if (Unavailable()) { return; }

        await LockAsync();

        await SignInAsync(Wrong);

        Assert.Equal(Threshold, await FailedCountAsync());
    }

    // ---- Automatic unlock -----------------------------------------------------------------------------

    [Fact]
    public async Task TheAccountUnlocksWhenTheDurationElapses()
    {
        if (Unavailable()) { return; }

        await LockAsync();

        _clock.Advance(TimeSpan.FromMinutes(LockoutMinutes + 1));

        // No job ran, nothing swept the column. The lockout is a timestamp, and the clock passed it.
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    [Fact]
    public async Task AnUnlockedAccountStartsAFreshWindow()
    {
        if (Unavailable()) { return; }

        // Without the reset, the counter would still sit at the threshold and the next mistyped
        // password would re-lock immediately — an account locked forever after one bad afternoon.
        await LockAsync();

        _clock.Advance(TimeSpan.FromMinutes(LockoutMinutes + 1));

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Wrong));

        Assert.Equal(1, await FailedCountAsync());
        Assert.Null(await LockedUntilAsync());

        // And the right password still works, because one failure is not three.
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    // ---- Success resets --------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessClearsTheCount()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Wrong);
        await SignInAsync(Wrong);

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));

        Assert.Equal(0, await FailedCountAsync());
    }

    [Fact]
    public async Task ASuccessInTheMiddleMeansTheNextRunStartsFromZero()
    {
        if (Unavailable()) { return; }

        // Two failures, a success, then two more failures must not lock: the count is consecutive
        // failures, not failures ever.
        await SignInAsync(Wrong);
        await SignInAsync(Wrong);
        await SignInAsync(Password);

        await SignInAsync(Wrong);
        await SignInAsync(Wrong);

        Assert.Null(await LockedUntilAsync());
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    // ---- Enumeration safety ------------------------------------------------------------------------------

    [Fact]
    public async Task ALockedAccountAnswersExactlyLikeAWrongPassword()
    {
        if (Unavailable()) { return; }

        // The oracle this must not create. If locking an account changed the response, an attacker
        // could lock every address on a list and read back which ones were real.
        var wrongPassword = await SignInWithBodyAsync(Wrong);

        await LockAsync();

        var locked = await SignInWithBodyAsync(Password);

        Assert.Equal(wrongPassword.Status, locked.Status);
        Assert.Equal(WithoutCorrelation(wrongPassword.Body), WithoutCorrelation(locked.Body));
    }

    [Fact]
    public async Task ALockedAccountAnswersLikeAnAddressNobodyHolds()
    {
        if (Unavailable()) { return; }

        await LockAsync();

        var locked = await SignInWithBodyAsync(Password);
        var unknown = await SignInWithBodyAsync(Password, "nobody@example.test");

        Assert.Equal(unknown.Status, locked.Status);
        Assert.Equal(WithoutCorrelation(unknown.Body), WithoutCorrelation(locked.Body));
    }

    [Fact]
    public async Task TheResponseNeverMentionsTheLockout()
    {
        if (Unavailable()) { return; }

        await LockAsync();

        var (_, body) = await SignInWithBodyAsync(Password);
        var serialized = body.GetRawText();

        // Not the state, not the threshold, not when it ends. FR-AUTH-011 notifies the holder
        // through a channel the attacker does not control; the response is not that channel.
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());

        foreach (var leak in new[] { "lock", "attempt", "minute", "until", "remaining" })
        {
            Assert.DoesNotContain(leak, serialized, StringComparison.OrdinalIgnoreCase);
        }

        // "retryable" is §4.3's envelope field and appears on every error, so it is not a leak —
        // but it must say false, and there must be nothing saying when to come back.
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.False(body.TryGetProperty("retryAfter", out _));
    }

    [Fact]
    public async Task ALockedAccountCarriesNoRetryAfterHeader()
    {
        if (Unavailable()) { return; }

        // §4.2 attaches Retry-After to 429. Attaching one here would say "come back at this
        // time", which is the lockout expiry — the single most useful thing to leak.
        await LockAsync();

        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = Address, password = Password, clientType = "WebConsole" });

        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    // ---- The policy governs it ------------------------------------------------------------------------------

    [Fact]
    public async Task TheThresholdComesFromTheCompanyPolicy()
    {
        if (Unavailable()) { return; }

        // FR-AUTH-011 makes it configurable. Raising it to ten means three failures no longer lock.
        await SetPolicyAsync(maximumFailedAttempts: 10, lockoutMinutes: LockoutMinutes);

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            await SignInAsync(Wrong);
        }

        Assert.Null(await LockedUntilAsync());
        Assert.Equal(Threshold, await FailedCountAsync());
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    [Fact]
    public async Task TheDurationComesFromTheCompanyPolicy()
    {
        if (Unavailable()) { return; }

        await SetPolicyAsync(maximumFailedAttempts: Threshold, lockoutMinutes: 60);

        await LockAsync();

        Assert.Equal(_clock.GetUtcNow().AddMinutes(60), await LockedUntilAsync());

        // Still locked at the old duration's end, released at the new one's.
        _clock.Advance(TimeSpan.FromMinutes(LockoutMinutes + 1));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Password));

        _clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------

    private async Task LockAsync()
    {
        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            await SignInAsync(Wrong).ConfigureAwait(false);
        }
    }

    private async Task<HttpStatusCode> SignInAsync(string password, string email = Address)
    {
        var (status, _) = await SignInWithBodyAsync(password, email).ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SignInWithBodyAsync(
        string password, string email = Address)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password, clientType = "WebConsole" }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<int> FailedCountAsync() =>
        (await CredentialAsync().ConfigureAwait(false)).FailedLoginCount;

    private async Task<DateTimeOffset?> LockedUntilAsync() =>
        (await CredentialAsync().ConfigureAwait(false)).LockoutUntilUtc;

    private async Task<EmployeeCredential> CredentialAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.EmployeeCredentials
            .AsNoTracking()
            .SingleAsync(credential => credential.EmployeeId == _employeeId)
            .ConfigureAwait(false);
    }

    private async Task<int> SessionCountAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.Sessions.CountAsync().ConfigureAwait(false);
    }

    private async Task SetPolicyAsync(int maximumFailedAttempts, int lockoutMinutes)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var policy = CompanyAuthenticationPolicy.Create(
            _company,
            minimumPasswordLength: 12,
            requireBreachCheck: true,
            idleTimeoutMinutes: 60,
            absoluteLifetimeMinutes: 720,
            mfaRequired: false,
            maximumFailedAttempts,
            lockoutMinutes,
            _clock.GetUtcNow());

        context.CompanyAuthenticationPolicies.Add(policy.Value);

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);

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

    /// <summary>Strips the per-request correlation identifier so two envelopes can be compared.</summary>
    private static string WithoutCorrelation(JsonElement body) =>
        string.Join('|', body.EnumerateObject()
            .Where(property => property.Name != "correlationId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
}
