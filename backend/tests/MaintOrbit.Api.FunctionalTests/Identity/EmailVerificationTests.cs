using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Notifications;
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
/// Drives email verification end to end (FR-AUTH-013).
/// </summary>
/// <remarks>
/// These need a real database: issuing resolves a Company from a session, redeeming resolves one
/// from a token alone, and both write under row-level security. They are skipped when no PostgreSQL
/// is reachable.
/// <para>
/// The notifier is replaced with one that captures the token, because that is the only way a test
/// can hold what an Employee would receive by email. Nothing else is faked: the tokens are real and
/// the isolation is the database's.
/// </para>
/// </remarks>
public sealed class EmailVerificationTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly CapturingNotifier _notifier = new();
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
        _token = await SignInAsync().ConfigureAwait(false);
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
                            ["EmailVerification:LifetimeMinutes"] = "1440"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    // The real notifier delivers nothing (TD-4), so without this a test could never
                    // hold the token an Employee would be sent.
                    services.AddSingleton<IEmailVerificationNotifier>(_notifier);

                    // Expiry is a rule about time, and a test that waited a day is a test nobody
                    // runs.
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

    // ---- Issuing -----------------------------------------------------------------------------------

    [Fact]
    public async Task RequestingVerificationIsAcceptedAndIssuesAToken()
    {
        if (Unavailable()) { return; }

        var status = await RequestAsync();

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.NotNull(_notifier.LastToken);

        // Sent to the address being verified, and to no other.
        Assert.Equal(Address, _notifier.LastRecipient?.Value);

        // Time-limited, bounded by the configured window rather than left open.
        Assert.True(_notifier.LastExpiry > _clock.GetUtcNow());
        Assert.True(_notifier.LastExpiry <= _clock.GetUtcNow().AddMinutes(1_440));
    }

    [Fact]
    public async Task RequestingRequiresASession()
    {
        if (Unavailable()) { return; }

        // Without one, a caller could have verification mail sent to anybody.
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsync(
            "/api/v1/auth/email/verify/request", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_notifier.LastToken);
    }

    [Fact]
    public async Task RequestingTwiceInvalidatesTheFirstLink()
    {
        if (Unavailable()) { return; }

        // Otherwise every request leaves another live proof of an address behind, and none of them
        // is distinguishable from the newest.
        await RequestAsync();
        var first = _notifier.LastToken!;

        await RequestAsync();
        var second = _notifier.LastToken!;

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await VerifyAsync(first));
        Assert.Equal(HttpStatusCode.NoContent, await VerifyAsync(second));
    }

    // ---- Redeeming ----------------------------------------------------------------------------------

    [Fact]
    public async Task AValidTokenVerifiesTheAddress()
    {
        if (Unavailable()) { return; }

        await UnverifyAsync();

        await RequestAsync();

        Assert.Equal(HttpStatusCode.NoContent, await VerifyAsync(_notifier.LastToken!));

        Assert.NotNull(await VerifiedAtAsync());
    }

    [Fact]
    public async Task RedeemingRequiresNoSession()
    {
        if (Unavailable()) { return; }

        // The link is opened from an email in whatever browser is to hand. Verification gates
        // activation, so requiring a session would make it reachable only by people who are
        // already active.
        await RequestAsync();

        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/email/verify", new { token = _notifier.LastToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AUsedTokenIsRefusedOnReplay()
    {
        if (Unavailable()) { return; }

        await RequestAsync();
        var token = _notifier.LastToken!;

        Assert.Equal(HttpStatusCode.NoContent, await VerifyAsync(token));
        Assert.Equal(HttpStatusCode.Unauthorized, await VerifyAsync(token));
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        if (Unavailable()) { return; }

        await UnverifyAsync();
        await RequestAsync();

        _clock.Advance(TimeSpan.FromMinutes(1_441));

        Assert.Equal(HttpStatusCode.Unauthorized, await VerifyAsync(_notifier.LastToken!));

        // And the address is still unproved.
        Assert.Null(await VerifiedAtAsync());
    }

    [Fact]
    public async Task AnUnknownTokenIsRefusedTheSameWayAsAUsedOne()
    {
        if (Unavailable()) { return; }

        await RequestAsync();
        var used = _notifier.LastToken!;
        await VerifyAsync(used);

        var unknown = await VerifyWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g");
        var replayed = await VerifyWithBodyAsync(used);

        // Distinguishing them would tell whoever is probing which of their guesses was a real
        // token that had merely been spent.
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.Status);
        Assert.Equal(WithoutCorrelation(unknown.Body), WithoutCorrelation(replayed.Body));
    }

    [Fact]
    public async Task AMissingTokenIsAValidationFailure()
    {
        if (Unavailable()) { return; }

        var (status, body) = await VerifyWithBodyAsync(string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task AFailureLeaksNothingAboutTheAccount()
    {
        if (Unavailable()) { return; }

        var (_, body) = await VerifyWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g");
        var serialized = body.GetRawText();

        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
        Assert.DoesNotContain(Address, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("employee", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
    }

    // ---- It proves one address ------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIssuedForAnAddressDoesNotVerifyADifferentOne()
    {
        if (Unavailable()) { return; }

        // The property that makes this a verification rather than a formality: a link sent to an
        // old address must not verify whatever replaced it.
        await UnverifyAsync();
        await RequestAsync();
        var token = _notifier.LastToken!;

        await ChangeAddressAsync("ada.new@example.test");

        Assert.Equal(HttpStatusCode.Unauthorized, await VerifyAsync(token));
        Assert.Null(await VerifiedAtAsync());
    }

    [Fact]
    public async Task ATokenForASupersededAddressIsSpentRatherThanLeftLive()
    {
        if (Unavailable()) { return; }

        // Leaving it redeemable would keep a credential for an address the Employee no longer uses
        // in circulation — and it would verify the moment the address changed back.
        await UnverifyAsync();
        await RequestAsync();
        var token = _notifier.LastToken!;

        await ChangeAddressAsync("ada.new@example.test");
        await VerifyAsync(token);

        await ChangeAddressAsync(Address);

        Assert.Equal(HttpStatusCode.Unauthorized, await VerifyAsync(token));
    }

    // ---- Storage ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTokenIsStoredOnlyAsAHash()
    {
        if (Unavailable()) { return; }

        await RequestAsync();
        var token = _notifier.LastToken!;

        var stored = await SingleTokenAsync();

        // A database holding live verification tokens is a database holding proofs of address.
        Assert.NotEqual(token, stored.TokenHash.Value);
        Assert.Equal(EmailVerificationTokenHash.Length, stored.TokenHash.Value.Length);
        Assert.Equal(_company, stored.CompanyId);
        Assert.Equal(_employeeId, stored.EmployeeId);

        // And the address it was issued for is on the row, which is what makes the match possible.
        Assert.Equal(Address, stored.Email.Value);
    }

    [Fact]
    public async Task TheVerifiedInstantIsRecordedOnTheEmployee()
    {
        if (Unavailable()) { return; }

        await UnverifyAsync();
        await RequestAsync();
        await VerifyAsync(_notifier.LastToken!);

        Assert.Equal(_clock.GetUtcNow(), await VerifiedAtAsync());
    }

    [Fact]
    public async Task ReVerifyingKeepsTheFirstInstant()
    {
        if (Unavailable()) { return; }

        await UnverifyAsync();
        await RequestAsync();
        await VerifyAsync(_notifier.LastToken!);

        var first = await VerifiedAtAsync();

        // Five minutes, not a month. The bearer middleware validates a token's lifetime against
        // the system clock rather than the injected one, so a large advance makes every access
        // token this host issues look future-dated. Any gap proves the point: the recorded instant
        // is the first one, whatever happens later.
        _clock.Advance(TimeSpan.FromMinutes(5));

        await RequestAsync();
        Assert.Equal(HttpStatusCode.NoContent, await VerifyAsync(_notifier.LastToken!));

        // The column answers "how long has this address been trusted?", so it does not move.
        Assert.Equal(first, await VerifiedAtAsync());
    }

    [Fact]
    public async Task VerifyingDoesNotChangeTheEmployeesStatus()
    {
        if (Unavailable()) { return; }

        // Verification and activation are different facts. Proving an address does not activate an
        // account, and does not reinstate a suspended one.
        await RequestAsync();
        await VerifyAsync(_notifier.LastToken!);

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var scope = tenant.BeginTenantScope(_company);
        using var services = _host.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var employee = await context.Employees.SingleAsync(e => e.Id == _employeeId);

        Assert.Equal(Domain.Modules.Identity.Enums.EmployeeStatus.Active, employee.Status);
    }

    // ---- Pipeline ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEndpointsRunThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/auth/email/verify/request");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private async Task<HttpStatusCode> RequestAsync()
    {
        _notifier.Clear();

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/auth/email/verify/request");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        return response.StatusCode;
    }

    private async Task<HttpStatusCode> VerifyAsync(string token)
    {
        var (status, _) = await VerifyWithBodyAsync(token).ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> VerifyWithBodyAsync(string token)
    {
        using var client = _host!.GetTestClient();
        using var response = await client
            .PostAsJsonAsync("/api/v1/auth/email/verify", new { token })
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<string> SignInAsync()
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = Address, password = Password, clientType = "WebConsole" })
            .ConfigureAwait(false);

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<EmailVerificationToken> SingleTokenAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.EmailVerificationTokens
            .AsNoTracking()
            .OrderByDescending(token => token.IssuedAtUtc)
            .FirstAsync()
            .ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> VerifiedAtAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == _employeeId)
            .Select(employee => employee.EmailVerifiedAtUtc)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the verified instant, so a test can observe it being set.
    /// </summary>
    /// <remarks>
    /// Accepting an invitation verifies the address on its own, so the seeded Employee arrives
    /// already proved. There is no endpoint that un-verifies — nor should there be — so this
    /// writes the column directly.
    /// </remarks>
    private async Task UnverifyAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.Employees
            .Where(employee => employee.Id == _employeeId)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(employee => employee.EmailVerifiedAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the Employee's address.
    /// </summary>
    /// <remarks>
    /// Written directly because no address-change use case exists — which is exactly why the token
    /// records the address it was issued for now rather than when that flow is built. Auditing
    /// every token issued before the check existed is not a migration anyone wants to write.
    /// </remarks>
    private async Task ChangeAddressAsync(string address)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.Employees
            .Where(employee => employee.Id == _employeeId)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(employee => employee.Email, Email.Create(address)))
            .ConfigureAwait(false);
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

    /// <summary>Captures what the Employee would have been emailed.</summary>
    private sealed class CapturingNotifier : IEmailVerificationNotifier
    {
        public string? LastToken { get; private set; }

        public Email? LastRecipient { get; private set; }

        public DateTimeOffset LastExpiry { get; private set; }

        public void Clear()
        {
            LastToken = null;
            LastRecipient = null;
            LastExpiry = default;
        }

        public Task SendAsync(
            Email recipient,
            EmployeeId employeeId,
            string token,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken)
        {
            LastToken = token;
            LastRecipient = recipient;
            LastExpiry = expiresAtUtc;

            return Task.CompletedTask;
        }
    }
}
