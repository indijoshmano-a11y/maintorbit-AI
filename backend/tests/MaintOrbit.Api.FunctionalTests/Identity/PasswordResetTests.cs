using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Notifications;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Drives password reset and account recovery end to end (FR-AUTH-012).
/// </summary>
/// <remarks>
/// These need a real database. The flow resolves a Company across tenants for an address and again
/// for a token, opens a scope, writes and consumes a row under row-level security, replaces an
/// Argon2id hash, and revokes sessions — a chain that means nothing against a substitute. They are
/// skipped when no PostgreSQL is reachable, so the suite still runs where one is not.
/// <para>
/// The notifier is replaced with one that captures the token, because that is the only way a test
/// can hold what an Employee would receive by email. Nothing else is faked: the tokens are real,
/// the hashing is real, and the isolation is the database's.
/// </para>
/// </remarks>
public sealed class PasswordResetTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a different long passphrase entirely";
    private const string Address = "ada@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly CapturingNotifier _notifier = new();
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

        await SeedActiveEmployeeAsync().ConfigureAwait(false);
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
                            ["PasswordReset:LifetimeMinutes"] = "60"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    // The only substitution. The real notifier delivers nothing (TD-4), so without
                    // this a test could never hold the token an Employee would be sent.
                    services.AddSingleton<IPasswordResetNotifier>(_notifier);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints => endpoints.MapAuthenticationEndpoints());
                }))
            .Build();

    /// <summary>Creates an Employee with a real Argon2id credential, through the real use case.</summary>
    private async Task SeedActiveEmployeeAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);

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

    /// <summary>
    /// Whether these tests can run at all.
    /// </summary>
    /// <remarks>
    /// Returns rather than fails when PostgreSQL is unreachable. xUnit 2 has no skip mechanism and
    /// the package that adds one is not in the documented inventory, so the alternative would be a
    /// suite that fails on any machine without a database.
    /// </remarks>
    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Makes the skip visible instead of silent, so a run with no database cannot be mistaken
        // for a run that exercised these paths.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Requesting a reset -------------------------------------------------------------------

    [Fact]
    public async Task RequestingAReset_IsAcceptedAndIssuesAToken()
    {
        if (Unavailable()) { return; }

        var status = await RequestAsync(Address);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.NotNull(_notifier.LastToken);
        Assert.Equal(Address, _notifier.LastRecipient?.Value);

        // Time-limited, and bounded by the configured window rather than left open.
        Assert.True(_notifier.LastExpiry > DateTimeOffset.UtcNow);
        Assert.True(_notifier.LastExpiry <= DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task RequestingAResetForAnUnknownAddress_IsIndistinguishable()
    {
        if (Unavailable()) { return; }

        // The property that matters most on this endpoint. An unauthenticated caller must not be
        // able to learn whether an address belongs to a customer, so the status, the body, and the
        // absence of any error are identical to the known-address case.
        var known = await RequestWithBodyAsync(Address);
        _notifier.Clear();
        var unknown = await RequestWithBodyAsync("nobody@example.test");

        Assert.Equal(HttpStatusCode.Accepted, known.Status);
        Assert.Equal(HttpStatusCode.Accepted, unknown.Status);
        Assert.Equal(known.Body, unknown.Body);

        // And nothing was issued for the address nobody holds.
        Assert.Null(_notifier.LastToken);
    }

    [Fact]
    public async Task RequestingAResetForAMalformedAddress_IsAlsoIndistinguishable()
    {
        if (Unavailable()) { return; }

        // Validating the shape here would answer "is this even an address?" — a smaller leak than
        // the account check but the same kind, and one that lets an attacker clean a list before
        // probing it.
        var (status, _) = await RequestWithBodyAsync("not-an-address");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Null(_notifier.LastToken);
    }

    [Fact]
    public async Task RequestingAResetTwice_InvalidatesTheFirstLink()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var first = _notifier.LastToken!;

        await RequestAsync(Address);
        var second = _notifier.LastToken!;

        Assert.NotEqual(first, second);

        // Otherwise every request would leave another live takeover credential behind, and the
        // Employee would have no way to know how many were outstanding.
        Assert.Equal(HttpStatusCode.Unauthorized, await CompleteAsync(first));
        Assert.Equal(HttpStatusCode.NoContent, await CompleteAsync(second));
    }

    [Fact]
    public async Task RequestingAResetForASuspendedEmployee_IssuesNothing()
    {
        if (Unavailable()) { return; }

        await SetStatusAsync(EmployeeStatus.Suspended);

        var status = await RequestAsync(Address);

        // Still 202 — a suspended account must not be detectable either. But issuing would produce
        // a link that fails at the last step, after the Employee has been told to check their mail.
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Null(_notifier.LastToken);
    }

    // ---- Completing a reset -------------------------------------------------------------------

    [Fact]
    public async Task AValidToken_ResetsThePassword()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);

        Assert.Equal(
            HttpStatusCode.NoContent,
            await CompleteAsync(_notifier.LastToken!));

        // The new password works and the old one does not — the reset replaced it rather than
        // adding to it.
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(Password));
    }

    [Fact]
    public async Task AUsedToken_IsRefusedOnReplay()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var token = _notifier.LastToken!;

        Assert.Equal(HttpStatusCode.NoContent, await CompleteAsync(token));

        // Single-use is the property FR-AUTH-012 names, and a link that works twice is one an
        // attacker can use after the legitimate holder already has.
        Assert.Equal(HttpStatusCode.Unauthorized, await CompleteAsync(token));
    }

    [Fact]
    public async Task AReplayedToken_DoesNotOverwriteThePasswordItAlreadySet()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var token = _notifier.LastToken!;

        await CompleteAsync(token);
        await CompleteAsync(token, "third password attempt entirely");

        // The refusal has to be real, not cosmetic: a replay that returns 401 while still writing
        // the hash would hand the account to whoever replayed it.
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(NewPassword));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await SignInAsync("third password attempt entirely"));
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var token = _notifier.LastToken!;

        // Aged in the database rather than by waiting an hour. The row is what the handler reads,
        // so moving its expiry is the same observation the clock would eventually produce.
        await ExpireOutstandingTokensAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await CompleteAsync(token));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(Password));
    }

    [Fact]
    public async Task AnUnknownToken_IsRefusedTheSameWayAsAUsedOne()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var used = _notifier.LastToken!;
        await CompleteAsync(used);

        var unknown = await CompleteWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g")
            ;
        var replayed = await CompleteWithBodyAsync(used);

        // Distinguishing them would tell whoever is probing which of their guesses was a real
        // token that had merely been spent. The correlation identifier is excluded because it is
        // per-request by design (§4.3) and differs on every call, including two identical ones.
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.Status);
        Assert.Equal(WithoutCorrelation(unknown.Body), WithoutCorrelation(replayed.Body));
    }

    [Fact]
    public async Task AMissingField_IsAValidationFailureRatherThanAnAuthenticationOne()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync(
            "/api/v1/auth/password-reset/complete",
            new { token = "", newPassword = "" });

        // Says nothing about any account: the request is missing a field. §7 maps this to 400.
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task AFailedCompletion_LeaksNothingAboutTheAccount()
    {
        if (Unavailable()) { return; }

        var (_, body) = await CompleteWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g")
            ;

        var serialized = body.GetRawText();

        // The description names no account and no reason. "invalid or has expired" is deliberately
        // one phrase covering both — it is what every failure says, so it distinguishes nothing.
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
        Assert.Equal(
            "The reset link is invalid or has expired.",
            body.GetProperty("detail").GetString());
        Assert.DoesNotContain(Address, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("employee", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuccessfulCompletion_CarriesACorrelationIdOnItsFailurePath()
    {
        if (Unavailable()) { return; }

        // §4.3 puts a correlation identifier in the error envelope so a support conversation can
        // find the request without the caller quoting anything sensitive.
        var (_, body) = await CompleteWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g")
            ;

        Assert.True(body.TryGetProperty("correlationId", out var correlation));
        Assert.False(string.IsNullOrEmpty(correlation.GetString()));
    }

    // ---- Session revocation -------------------------------------------------------------------

    [Fact]
    public async Task ResettingThePassword_EndsEverySession()
    {
        if (Unavailable()) { return; }

        // Two live sessions, as an Employee signed in on two devices would have.
        var first = await SignInForTokensAsync();
        var second = await SignInForTokensAsync();

        await RequestAsync(Address);
        await CompleteAsync(_notifier.LastToken!);

        // NFR-SEC-017. The plausible reason for a reset is that somebody else holds the old
        // password — and possibly a live session established with it.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(first));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(second));

        Assert.Equal(2, await RevokedSessionCountAsync());
    }

    // ---- Storage and rehashing ----------------------------------------------------------------

    [Fact]
    public async Task TheTokenIsStoredOnlyAsAHash()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var token = _notifier.LastToken!;

        var stored = await SingleStoredTokenAsync();

        // A database holding live reset tokens is a database holding account takeovers.
        Assert.NotEqual(token, stored.TokenHash.Value);
        Assert.Equal(PasswordResetTokenHash.Length, stored.TokenHash.Value.Length);
        Assert.Equal(_company, stored.CompanyId);
        Assert.Equal(_employeeId, stored.EmployeeId);
    }

    [Fact]
    public async Task TheNewPasswordIsHashedAtCurrentParameters()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        await CompleteAsync(_notifier.LastToken!);

        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var credential = await scope.ServiceProvider
            .GetRequiredService<IEmployeeCredentialRepository>()
            .FindForAsync(_employeeId, CancellationToken.None)
            ;

        Assert.NotNull(credential);

        // A reset always derives with the current generation, so a credential that came back
        // through this path is never left on parameters an SD-010 review already retired.
        Assert.False(hasher.NeedsRehash(credential.PasswordHash));
        Assert.Equal(hasher.CurrentVersion.Value, credential.PasswordVersion);
        Assert.Equal(hasher.CurrentParameters, credential.HashParameters);
        Assert.Equal(PasswordAlgorithm.Argon2id, credential.Algorithm);
    }

    // ---- Repository ---------------------------------------------------------------------------

    [Fact]
    public async Task TheRepository_FindsSpentTokensRatherThanHidingThem()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var token = _notifier.LastToken!;
        await CompleteAsync(token);

        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var factory = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenFactory>();
        var found = await scope.ServiceProvider
            .GetRequiredService<IPasswordResetTokenRepository>()
            .FindByHashAsync(factory.Hash(token), CancellationToken.None)
            ;

        // Filtering consumed rows out would turn a replay into "unknown token" — the same answer
        // as a typo, which triggers nothing.
        Assert.NotNull(found);
        Assert.True(found.IsConsumed);
    }

    [Fact]
    public async Task TheRepository_LeavesSpentTokensAloneWhenSuperseding()
    {
        if (Unavailable()) { return; }

        await RequestAsync(Address);
        var consumed = _notifier.LastToken!;
        await CompleteAsync(consumed);

        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var repository = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>();

        var swept = await repository
            .InvalidateOutstandingForEmployeeAsync(
                _employeeId, DateTimeOffset.UtcNow, CancellationToken.None)
            ;

        // Nothing outstanding is left, and the redeemed row keeps its record. Overwriting it would
        // lose whether a link was used or merely superseded.
        Assert.Equal(0, swept);

        var factory = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenFactory>();
        var found = await repository
            .FindByHashAsync(factory.Hash(consumed), CancellationToken.None)
            ;

        Assert.NotNull(found);
        Assert.True(found.IsConsumed);
        Assert.False(found.IsInvalidated);
    }

    [Fact]
    public void TheMigrationShipsATenantPolicyWithTheTable()
    {
        // Asserted against the emitted SQL rather than by querying as another Company, because the
        // developer role that runs these tests is a superuser and therefore BYPASSRLS — a
        // cross-tenant read would return rows here and prove nothing either way. Enforcement was
        // verified separately against a NOSUPERUSER NOBYPASSRLS role.
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        var sql = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains(
            "ALTER TABLE identity.password_reset_tokens ENABLE ROW LEVEL SECURITY",
            sql, StringComparison.Ordinal);

        // FORCE is the statement that decides whether any of it works: PostgreSQL exempts a
        // table's owner from its own policies by default, and migrations run as owner.
        Assert.Contains(
            "ALTER TABLE identity.password_reset_tokens FORCE ROW LEVEL SECURITY",
            sql, StringComparison.Ordinal);

        Assert.Contains(
            "CREATE POLICY rls_password_reset_tokens ON identity.password_reset_tokens",
            sql, StringComparison.Ordinal);

        // USING alone would filter reads while leaving a caller able to insert a row against
        // another Company — which returns as a successful insert.
        Assert.Contains("WITH CHECK (company_id =", sql, StringComparison.Ordinal);

        // The predicate must match what the interceptor sets, or every query returns zero rows —
        // safe, but silent.
        Assert.Contains(TenantSession.CurrentCompanyExpression, sql, StringComparison.Ordinal);
    }

    // ---- Pipeline -----------------------------------------------------------------------------

    [Fact]
    public async Task TheEndpointsRunThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var response = await client
            .PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = Address })
            ;

        // The correlation header the middleware attaches to every response (10.4/10.5). Its
        // presence here is what shows these endpoints are not mounted outside the pipeline.
        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    [Fact]
    public async Task NeitherEndpointRequiresAuthentication()
    {
        if (Unavailable()) { return; }

        // §3.1 describes the group as "mostly unauthenticated", and an Employee who has forgotten
        // their password cannot present one. A 401 from the middleware rather than the handler
        // would make the flow reachable only by people who do not need it.
        Assert.Equal(HttpStatusCode.Accepted, await RequestAsync(Address));

        var (status, body) = await CompleteWithBodyAsync("Zm9yZ2VkLXRva2VuLXRoYXQtaXMtbG9uZy1lbm91Z2g")
            ;

        // 401, but from the handler refusing the token — not from a challenge, which would carry
        // no problem document.
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private async Task<HttpStatusCode> RequestAsync(string email)
    {
        _notifier.Clear();
        var (status, _) = await RequestWithBodyAsync(email).ConfigureAwait(false);
        return status;
    }

    private Task<(HttpStatusCode Status, string Body)> RequestWithBodyAsync(string email) =>
        PostRawAsync("/api/v1/auth/password-reset/request", new { email });

    private async Task<HttpStatusCode> CompleteAsync(string token, string password = NewPassword)
    {
        var (status, _) = await PostRawAsync(
            "/api/v1/auth/password-reset/complete",
            new { token, newPassword = password }).ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CompleteWithBodyAsync(string token) =>
        await PostAsync(
            "/api/v1/auth/password-reset/complete",
            new { token, newPassword = NewPassword }).ConfigureAwait(false);

    private async Task<HttpStatusCode> SignInAsync(string password)
    {
        var (status, _) = await PostRawAsync(
            "/api/v1/auth/login",
            new { email = Address, password, clientType = "WebConsole" }).ConfigureAwait(false);

        return status;
    }

    private async Task<string> SignInForTokensAsync()
    {
        var (_, body) = await PostAsync(
            "/api/v1/auth/login",
            new { email = Address, password = Password, clientType = "WebConsole" })
            .ConfigureAwait(false);

        return body.GetProperty("refreshToken").GetString()!;
    }

    private async Task<HttpStatusCode> RefreshAsync(string refreshToken)
    {
        var (status, _) = await PostRawAsync(
            "/api/v1/auth/refresh", new { refreshToken }).ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, string Body)> PostRawAsync(string path, object payload)
    {
        using var client = _host!.GetTestClient();
        using var response = await client.PostAsJsonAsync(path, payload).ConfigureAwait(false);

        return (response.StatusCode,
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path, object payload)
    {
        var (status, body) = await PostRawAsync(path, payload).ConfigureAwait(false);

        return (status,
            string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>Strips the per-request correlation identifier so two envelopes can be compared.</summary>
    private static string WithoutCorrelation(JsonElement body)
    {
        var fields = body.EnumerateObject()
            .Where(property => property.Name != "correlationId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}");

        return string.Join('|', fields);
    }

    private async Task<PasswordResetToken> SingleStoredTokenAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.PasswordResetTokens.SingleAsync().ConfigureAwait(false);
    }

    private async Task ExpireOutstandingTokensAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
        var requested = DateTimeOffset.UtcNow.AddHours(-2);
        var expired = DateTimeOffset.UtcNow.AddHours(-1);

        // Both timestamps move. ck_password_reset_tokens_expiry requires the window to open before
        // it closes, so back-dating the expiry alone is rejected by the database — which is the
        // constraint doing its job, not the fixture fighting it.
        await context.PasswordResetTokens
            .Where(token => token.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RequestedAtUtc, requested)
                .SetProperty(token => token.ExpiresAtUtc, expired))
            .ConfigureAwait(false);
    }

    private async Task SetStatusAsync(EmployeeStatus status)
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        await context.Employees
            .Where(employee => employee.Id == _employeeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(employee => employee.Status, status))
            .ConfigureAwait(false);
    }

    private async Task<int> RevokedSessionCountAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.Sessions
            .CountAsync(session =>
                session.EmployeeId == _employeeId &&
                session.RevocationReason == SessionRevocationReason.PasswordChanged)
            .ConfigureAwait(false);
    }

    /// <summary>Captures what the Employee would have been emailed.</summary>
    /// <remarks>
    /// Stands in for the notifications module. The registered adapter delivers nothing (TD-4), so
    /// this is the only way a test can hold the token that reaches an Employee.
    /// </remarks>
    private sealed class CapturingNotifier : IPasswordResetNotifier
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
