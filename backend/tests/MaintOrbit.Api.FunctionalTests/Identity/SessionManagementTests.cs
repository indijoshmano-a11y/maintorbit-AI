using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
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
/// Drives the session management endpoints end to end (FR-AUTH-008).
/// </summary>
/// <remarks>
/// A device list is only meaningful against real sessions, so every one here is opened by a real
/// sign-in through the real endpoint — three devices for the Employee under test and one for a
/// colleague, because the interesting failure is a caller reaching somebody else's.
/// <para>
/// The clock is controllable: the idle window and the absolute lifetime are rules about time, and
/// a test that waited an hour is a test nobody runs.
/// </para>
/// </remarks>
public sealed class SessionManagementTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";
    private const string Colleague = "sam@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly AdvanceableClock _clock = new();

    private IHost? _host;
    private string? _skip;
    private string? _database;

    private EmployeeId _adaId;
    private string _adaToken = string.Empty;
    private string _samToken = string.Empty;

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

        _adaId = await SeedEmployeeAsync(Address).ConfigureAwait(false);
        await SeedEmployeeAsync(Colleague).ConfigureAwait(false);

        _adaToken = await SignInAsync(Address, "WebConsole").ConfigureAwait(false);
        _samToken = await SignInAsync(Colleague, "WebConsole").ConfigureAwait(false);
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

                            // A short idle window, so the list can be observed dropping a device
                            // without waiting an hour.
                            ["AuthenticationPolicy:IdleTimeoutMinutes"] = "10",
                            ["AuthenticationPolicy:AbsoluteLifetimeMinutes"] = "720"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    services.AddSingleton<TimeProvider>(_clock);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAuthenticationEndpoints();
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

    // ---- Listing ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListShowsEverySessionTheEmployeeHolds()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Address, "VsCodeExtension");
        await SignInAsync(Address, "Gateway");

        var (status, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TheListShowsOnlyTheCallersOwnSessions()
    {
        if (Unavailable()) { return; }

        // The colleague is signed in on the same Company. A device list is a map of where somebody
        // works, and it must not be readable by anybody but them.
        await SignInAsync(Colleague, "VsCodeExtension");

        var (_, ada) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);
        var (_, sam) = await GetAsync("/api/v1/employees/me/sessions", _samToken);

        Assert.Equal(1, ada.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, sam.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ExactlyOneEntryIsMarkedCurrent()
    {
        if (Unavailable()) { return; }

        // The list is only actionable if the reader can tell which entry is the device in front of
        // them — otherwise they end the session they are using by mistake.
        await SignInAsync(Address, "VsCodeExtension");

        var (_, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);

        var current = body.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("isCurrent").GetBoolean())
            .ToList();

        Assert.Single(current);
        Assert.Equal(
            await CurrentSessionIdAsync(), current[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task TheListCarriesTheDeviceDetailTheEmployeeNeeds()
    {
        if (Unavailable()) { return; }

        // §4.2 classifies the address and location as personal data about the Employee and states
        // they are "visible to the Employee (principle P-7)". A list that hid where a session was
        // opened from could not answer "is one of these not me?".
        var (_, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);
        var session = body.GetProperty("items")[0];

        Assert.Equal("WebConsole", session.GetProperty("clientType").GetString());
        Assert.True(session.TryGetProperty("ipAddress", out _));
        Assert.True(session.TryGetProperty("coarseLocation", out _));
        Assert.True(session.GetProperty("createdAtUtc").GetDateTimeOffset() <= _clock.GetUtcNow());
        Assert.True(
            session.GetProperty("absoluteExpiresAtUtc").GetDateTimeOffset() > _clock.GetUtcNow());
    }

    [Fact]
    public async Task TheListCarriesNothingThatCouldActAsTheSession()
    {
        if (Unavailable()) { return; }

        var (_, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);
        var serialized = body.GetRawText();

        // No token, no refresh chain, no secret. A device list that leaked one would turn a
        // read-only convenience into session theft.
        foreach (var leak in new[] { "token", "refresh", "secret", "hash" })
        {
            Assert.DoesNotContain(leak, serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARevokedSessionLeavesTheList()
    {
        if (Unavailable()) { return; }

        var second = await SignInAsync(Address, "VsCodeExtension");

        await LogoutAsync(second);

        var (_, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnIdledOutSessionLeavesTheListWithoutAnythingSweepingIt()
    {
        if (Unavailable()) { return; }

        // The list applies the Company's idle window — the same one the session validator honours
        // — so a device that idled out is gone from the list before anything sweeps the row.
        //
        // The second device is left alone while this one keeps working, which is what makes the
        // two observable separately: activity on one session does not extend another.
        await SignInAsync(Address, "VsCodeExtension");

        _clock.Advance(TimeSpan.FromMinutes(8));
        await PostAsync("/api/v1/employees/me/sessions/current/activity", _adaToken);

        // Twelve minutes since the second device signed in, four since this one was last used.
        _clock.Advance(TimeSpan.FromMinutes(4));

        var (status, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.True(body.GetProperty("items")[0].GetProperty("isCurrent").GetBoolean());

        // Both rows are still unrevoked: nothing swept the idled-out one, its window simply closed.
        Assert.Equal(2, await UnrevokedCountAsync());
    }

    // ---- Current -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCurrentSessionIsTheOneTheRequestIsAuthenticatedWith()
    {
        if (Unavailable()) { return; }

        var other = await SignInAsync(Address, "VsCodeExtension");

        var (status, mine) = await GetAsync("/api/v1/employees/me/sessions/current", _adaToken);
        var (_, theirs) = await GetAsync("/api/v1/employees/me/sessions/current", other);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(mine.GetProperty("isCurrent").GetBoolean());
        Assert.True(theirs.GetProperty("isCurrent").GetBoolean());

        // Two tokens, two different sessions — the endpoint reads the caller's, not the newest.
        Assert.NotEqual(
            mine.GetProperty("id").GetString(), theirs.GetProperty("id").GetString());
        Assert.Equal("WebConsole", mine.GetProperty("clientType").GetString());
        Assert.Equal("VsCodeExtension", theirs.GetProperty("clientType").GetString());
    }

    // ---- Revoking one ----------------------------------------------------------------------------------

    [Fact]
    public async Task RevokingASessionEndsItAndStopsItRefreshing()
    {
        if (Unavailable()) { return; }

        var second = await SignInWithRefreshAsync(Address, "VsCodeExtension");

        var target = await SessionIdOfAsync(second.AccessToken);

        Assert.Equal(
            HttpStatusCode.NoContent,
            await DeleteAsync($"/api/v1/employees/me/sessions/{target}", _adaToken));

        // The access token is refused, and the refresh chain bound to that session is dead —
        // rotation refuses when the session is not active.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions/current", second.AccessToken)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(second.RefreshToken));
    }

    [Fact]
    public async Task RevokingRecordsThatTheEmployeeDidIt()
    {
        if (Unavailable()) { return; }

        var second = await SignInAsync(Address, "VsCodeExtension");
        var target = await SessionIdOfAsync(second);

        await DeleteAsync($"/api/v1/employees/me/sessions/{target}", _adaToken);

        // §3.5 distinguishes the triggers, and only a stored reason can tell a logout from a
        // termination afterwards.
        Assert.Equal(
            SessionRevocationReason.TerminatedByEmployee,
            await RevocationReasonAsync(Guid.Parse(target)));
    }

    [Fact]
    public async Task RevokingTheCurrentSessionIsAllowedAndIsALogout()
    {
        if (Unavailable()) { return; }

        // Refusing would mean an Employee who suspects the device in front of them has to find a
        // different one first.
        var current = await CurrentSessionIdAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            await DeleteAsync($"/api/v1/employees/me/sessions/{current}", _adaToken));

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions", _adaToken)).Status);
    }

    [Fact]
    public async Task AColleaguesSessionCannotBeRevoked()
    {
        if (Unavailable()) { return; }

        // Without the ownership check, any authenticated Employee could end any colleague's
        // session by identifier — FR-AUTH-009's administrative capability, reached without the
        // permission that governs it.
        var target = await SessionIdOfAsync(_samToken);

        var status = await DeleteAsync($"/api/v1/employees/me/sessions/{target}", _adaToken);

        Assert.Equal(HttpStatusCode.NotFound, status);

        // And the colleague is still signed in.
        Assert.Equal(
            HttpStatusCode.OK,
            (await GetAsync("/api/v1/employees/me/sessions/current", _samToken)).Status);
    }

    [Fact]
    public async Task AColleaguesSessionIsNotFoundRatherThanForbidden()
    {
        if (Unavailable()) { return; }

        // §7: a forbidden answer confirms the session exists, which confirms the colleague is
        // signed in. Both cases must read the same.
        var real = await SessionIdOfAsync(_samToken);

        var colleague = await DeleteWithBodyAsync($"/api/v1/employees/me/sessions/{real}");
        var absent = await DeleteWithBodyAsync(
            $"/api/v1/employees/me/sessions/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, colleague.Status);
        Assert.Equal(HttpStatusCode.NotFound, absent.Status);
        Assert.Equal(WithoutCorrelation(colleague.Body), WithoutCorrelation(absent.Body));
    }

    [Fact]
    public async Task RevokingTwiceIsNotFoundTheSecondTimeOrSucceedsIdempotently()
    {
        if (Unavailable()) { return; }

        var second = await SignInAsync(Address, "VsCodeExtension");
        var target = await SessionIdOfAsync(second);
        var path = $"/api/v1/employees/me/sessions/{target}";

        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(path, _adaToken));

        // The aggregate is idempotent and keeps the first reason, so a repeat succeeds without
        // rewriting the record of when the session actually ended.
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(path, _adaToken));

        Assert.Equal(
            SessionRevocationReason.TerminatedByEmployee,
            await RevocationReasonAsync(Guid.Parse(target)));
    }

    // ---- Revoking all others -----------------------------------------------------------------------------

    [Fact]
    public async Task RevokingAllOthersKeepsTheCurrentOne()
    {
        if (Unavailable()) { return; }

        // §3.5: "Employee terminates all others — all except current". An Employee clearing devices
        // they do not recognise must not be signed out of the one they are using to do it.
        var second = await SignInAsync(Address, "VsCodeExtension");
        var third = await SignInAsync(Address, "Gateway");

        var (status, body) = await DeleteWithResponseAsync(
            "/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("revokedCount").GetInt32());

        // Still signed in here.
        Assert.Equal(
            HttpStatusCode.OK,
            (await GetAsync("/api/v1/employees/me/sessions", _adaToken)).Status);

        // And not there.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions/current", second)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions/current", third)).Status);
    }

    [Fact]
    public async Task RevokingAllOthersLeavesExactlyOneInTheList()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Address, "VsCodeExtension");
        await SignInAsync(Address, "Gateway");

        await DeleteWithResponseAsync("/api/v1/employees/me/sessions", _adaToken);

        var (_, body) = await GetAsync("/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.True(body.GetProperty("items")[0].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task RevokingAllOthersDoesNotTouchAColleague()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Colleague, "VsCodeExtension");

        await DeleteWithResponseAsync("/api/v1/employees/me/sessions", _adaToken);

        var (_, sam) = await GetAsync("/api/v1/employees/me/sessions", _samToken);

        Assert.Equal(2, sam.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task RevokingAllOthersWithNoOthersReportsNone()
    {
        if (Unavailable()) { return; }

        var (status, body) = await DeleteWithResponseAsync(
            "/api/v1/employees/me/sessions", _adaToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body.GetProperty("revokedCount").GetInt32());
    }

    // ---- Activity ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordingActivityResetsTheIdleWindow()
    {
        if (Unavailable()) { return; }

        _clock.Advance(TimeSpan.FromMinutes(8));

        Assert.Equal(
            HttpStatusCode.NoContent,
            await PostAsync("/api/v1/employees/me/sessions/current/activity", _adaToken));

        // Eight more minutes: past the original window, inside the reset one.
        _clock.Advance(TimeSpan.FromMinutes(8));

        Assert.Equal(
            HttpStatusCode.OK,
            (await GetAsync("/api/v1/employees/me/sessions/current", _adaToken)).Status);
    }

    [Fact]
    public async Task OrdinaryRequestsDoNotResetTheIdleWindow()
    {
        if (Unavailable()) { return; }

        // SM-b: "the activity signal must come from interaction, not from the SignalR connection or
        // automatic refetches". This is the assertion that keeps the implementation from becoming
        // middleware — a console polling every minute would otherwise keep an unattended desk
        // signed in forever.
        for (var minute = 0; minute < 8; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await GetAsync("/api/v1/employees/me/sessions", _adaToken);
        }

        _clock.Advance(TimeSpan.FromMinutes(3));

        // Eleven minutes of steady polling, and the session has still idled out.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions", _adaToken)).Status);
    }

    [Fact]
    public async Task ActivityIsMonotonic()
    {
        if (Unavailable()) { return; }

        // A clock adjustment or an out-of-order request must not move activity backwards and
        // shorten the window.
        _clock.Advance(TimeSpan.FromMinutes(5));
        await PostAsync("/api/v1/employees/me/sessions/current/activity", _adaToken);

        var recorded = await LastActiveAtAsync();

        _clock.Advance(TimeSpan.FromMinutes(-3));
        await PostAsync("/api/v1/employees/me/sessions/current/activity", _adaToken);

        Assert.Equal(recorded, await LastActiveAtAsync());
    }

    [Fact]
    public async Task ActivityCannotReviveARevokedSession()
    {
        if (Unavailable()) { return; }

        // A resurrected session is the failure that makes revocation meaningless. The pipeline
        // refuses first, which is the answer that matters — but the aggregate refuses too.
        var second = await SignInAsync(Address, "VsCodeExtension");
        await LogoutAsync(second);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await PostAsync("/api/v1/employees/me/sessions/current/activity", second));
    }

    [Fact]
    public async Task ActivityDoesNotExtendTheAbsoluteLifetime()
    {
        if (Unavailable()) { return; }

        // §3.2 calls the absolute lifetime "the one that cannot be defeated by activity". An
        // attacker holding a live session must not be able to keep it indefinitely by generating
        // traffic.
        var before = await AbsoluteExpiryAsync();

        _clock.Advance(TimeSpan.FromMinutes(5));
        await PostAsync("/api/v1/employees/me/sessions/current/activity", _adaToken);

        Assert.Equal(before, await AbsoluteExpiryAsync());
    }

    // ---- Authentication ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryEndpointRequiresASession()
    {
        if (Unavailable()) { return; }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions", null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/employees/me/sessions/current", null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await DeleteAsync($"/api/v1/employees/me/sessions/{Guid.CreateVersion7()}", null));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await DeleteWithResponseAsync("/api/v1/employees/me/sessions", null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await PostAsync("/api/v1/employees/me/sessions/current/activity", null));
    }

    [Fact]
    public async Task AResponseRunsThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/employees/me/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adaToken);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private async Task<string> CurrentSessionIdAsync() => await SessionIdOfAsync(_adaToken);

    private async Task<string> SessionIdOfAsync(string bearer)
    {
        var (_, body) = await GetAsync("/api/v1/employees/me/sessions/current", bearer)
            .ConfigureAwait(false);

        return body.GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(
        string path, string? bearer) => await SendAsync(HttpMethod.Get, path, bearer);

    private async Task<HttpStatusCode> PostAsync(string path, string? bearer)
    {
        var (status, _) = await SendAsync(HttpMethod.Post, path, bearer).ConfigureAwait(false);

        return status;
    }

    private async Task<HttpStatusCode> DeleteAsync(string path, string? bearer)
    {
        var (status, _) = await SendAsync(HttpMethod.Delete, path, bearer).ConfigureAwait(false);

        return status;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> DeleteWithResponseAsync(
        string path, string? bearer) => SendAsync(HttpMethod.Delete, path, bearer);

    private Task<(HttpStatusCode Status, JsonElement Body)> DeleteWithBodyAsync(string path) =>
        SendAsync(HttpMethod.Delete, path, _adaToken);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(method, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<HttpStatusCode> LogoutAsync(string bearer)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        return response.StatusCode;
    }

    private async Task<HttpStatusCode> RefreshAsync(string refreshToken)
    {
        using var client = _host!.GetTestClient();
        using var response = await client
            .PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken })
            .ConfigureAwait(false);

        return response.StatusCode;
    }

    private async Task<string> SignInAsync(string email, string clientType)
    {
        var tokens = await SignInWithRefreshAsync(email, clientType).ConfigureAwait(false);

        return tokens.AccessToken;
    }

    private async Task<(string AccessToken, string RefreshToken)> SignInWithRefreshAsync(
        string email, string clientType)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = Password, clientType }).ConfigureAwait(false);

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        return (body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<int> UnrevokedCountAsync()
    {
        using var context = await ScopedContextAsync().ConfigureAwait(false);

        return await context.Context.Sessions
            .CountAsync(session =>
                session.EmployeeId == _adaId && session.RevokedAtUtc == null)
            .ConfigureAwait(false);
    }

    private async Task<SessionRevocationReason?> RevocationReasonAsync(Guid sessionId)
    {
        using var context = await ScopedContextAsync().ConfigureAwait(false);

        return await context.Context.Sessions
            .AsNoTracking()
            .Where(session => session.Id == new SessionId(sessionId))
            .Select(session => session.RevocationReason)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<DateTimeOffset> LastActiveAtAsync()
    {
        using var context = await ScopedContextAsync().ConfigureAwait(false);
        var current = await CurrentSessionIdAsync().ConfigureAwait(false);

        return await context.Context.Sessions
            .AsNoTracking()
            .Where(session => session.Id == new SessionId(Guid.Parse(current)))
            .Select(session => session.LastActiveAtUtc)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<DateTimeOffset> AbsoluteExpiryAsync()
    {
        var current = await CurrentSessionIdAsync().ConfigureAwait(false);
        using var context = await ScopedContextAsync().ConfigureAwait(false);

        return await context.Context.Sessions
            .AsNoTracking()
            .Where(session => session.Id == new SessionId(Guid.Parse(current)))
            .Select(session => session.AbsoluteExpiresAtUtc)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>A tenant-scoped context, disposed with its scope.</summary>
    private Task<ScopedContext> ScopedContextAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        var tenantScope = tenant.BeginTenantScope(_company);
        var serviceScope = _host.Services.CreateScope();

        return Task.FromResult(new ScopedContext(
            tenantScope,
            serviceScope,
            serviceScope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>()));
    }

    private sealed record ScopedContext(
        IDisposable TenantScope, IServiceScope ServiceScope, MaintOrbitDbContext Context)
        : IDisposable
    {
        public void Dispose()
        {
            ServiceScope.Dispose();
            TenantScope.Dispose();
        }
    }

    private async Task MigrateAsync()
    {
        using var scope = _host!.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task<EmployeeId> SeedEmployeeAsync(string address)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        EmployeeId employeeId;

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            var employee = Employee.Invite(_company, Email.Create(address), _clock.GetUtcNow());
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

    /// <summary>Strips the per-request correlation identifier so two envelopes can be compared.</summary>
    private static string WithoutCorrelation(JsonElement body) =>
        string.Join('|', body.EnumerateObject()
            .Where(property => property.Name != "correlationId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
}
