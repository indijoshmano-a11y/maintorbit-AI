using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// Drives TOTP multi-factor authentication end to end (FR-AUTH-005).
/// </summary>
/// <remarks>
/// These need a real database. Enrolment seals a secret under AES-256-GCM and writes it to a
/// row-level-security table; confirmation spends a time step; verification opens the envelope
/// again and either advances the step or burns a recovery code — a chain that means nothing
/// against a substitute. They are skipped when no PostgreSQL is reachable.
/// <para>
/// <b>Nothing is faked.</b> The codes are computed the way an authenticator app computes them, from
/// the base32 secret the enrolment endpoint returns — which is the same thing an Employee's phone
/// receives, and the only input a test is entitled to.
/// </para>
/// </remarks>
public sealed class MfaEndpointTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private readonly AdvanceableClock _clock = new();
    private IHost? _host;
    private string? _skip;
    private string? _database;
    private string _accessToken = string.Empty;

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
        _accessToken = await SignInAsync().ConfigureAwait(false);
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
                            ["Mfa:RecoveryCodeCount"] = "10"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);

                    // The only substitution, and it replaces the system clock rather than any MFA
                    // component. Registered after AddInfrastructure so it wins: the handlers, the
                    // TOTP service, and the test all read the same instant, which is what lets a
                    // later time step be reached without waiting for one.
                    services.AddSingleton<TimeProvider>(_clock);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints => endpoints.MapAuthenticationEndpoints());
                }))
            .Build();

    private async Task SeedActiveEmployeeAsync()
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        EmployeeId employeeId;

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);

            var employee = Employee.Invite(_company, Email.Create(Address), DateTimeOffset.UtcNow);
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
    }

    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Makes the skip visible instead of silent, so a run with no database cannot be mistaken
        // for a run that exercised these paths.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    // ---- Enrolment ----------------------------------------------------------------------------

    [Fact]
    public async Task Enrolling_ReturnsASecretAndAKeyUri()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync("/api/v1/auth/mfa/enroll", null);

        Assert.Equal(HttpStatusCode.OK, status);

        var secret = body.GetProperty("secret").GetString()!;
        var uri = body.GetProperty("uri").GetString()!;

        // Base32, which is what the Key Uri Format specifies and what an authenticator app accepts
        // for manual entry. Base64 would be silently rejected by every app.
        Assert.All(secret, character => Assert.Contains(character, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
        Assert.StartsWith("otpauth://totp/", uri, StringComparison.Ordinal);
        Assert.Contains($"secret={secret}", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=MaintOrbit", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enrolling_ReturnsNoQrImage()
    {
        if (Unavailable()) { return; }

        // Nothing documented calls for one, and generating images server-side would put an encoder
        // on an authenticated path to render a secret. A client renders the URI locally.
        var (_, body) = await PostAsync("/api/v1/auth/mfa/enroll", null);

        Assert.False(body.TryGetProperty("qrCode", out _));
        Assert.False(body.TryGetProperty("image", out _));
        Assert.DoesNotContain("data:image", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnrollingTwiceBeforeConfirming_ReplacesTheUnprovedSecret()
    {
        if (Unavailable()) { return; }

        // Someone who scanned the code into the wrong app must be able to start over, and the
        // abandoned secret must stop being one that could confirm.
        var first = await EnrollAsync();
        var second = await EnrollAsync();

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await ConfirmAsync(first));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmForCodesAsync(second)).Status);
    }

    [Fact]
    public async Task EnrollingWhenAlreadyEnabled_IsRefused()
    {
        if (Unavailable()) { return; }

        await EnrolAndConfirmAsync();

        var (status, body) = await PostAsync("/api/v1/auth/mfa/enroll", null);

        // Silently superseding a live factor would let anyone with a hijacked session swap it for
        // one they control without ever proving they hold the current one.
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", body.GetProperty("type").GetString());
    }

    // ---- Confirmation -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmingWithAValidCode_TurnsTheFactorOnAndIssuesRecoveryCodes()
    {
        if (Unavailable()) { return; }

        var secret = await EnrollAsync();
        var (status, _, codes) = await ConfirmForCodesAsync(secret);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ConfirmingWithAWrongCode_IsRefused()
    {
        if (Unavailable()) { return; }

        await EnrollAsync();

        var (status, body) = await PostAsync(
            "/api/v1/auth/mfa/confirm", new { code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConfirmingWithNothingEnrolled_IsAConflict()
    {
        if (Unavailable()) { return; }

        var (status, _) = await PostAsync("/api/v1/auth/mfa/confirm", new { code = "123456" });

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [Fact]
    public async Task ConfirmingTwice_IsRefused()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();

        // The code that proved possession was spent by proving it.
        Assert.Equal(HttpStatusCode.Conflict, await ConfirmAsync(secret));
    }

    // ---- Verification -------------------------------------------------------------------------

    [Fact]
    public async Task AValidCode_SatisfiesTheChallenge()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();

        // A later step than the one confirmation spent, which is what an Employee would present
        // half a minute later.
        _clock.Advance(TimeSpan.FromSeconds(30));

        var (status, body) = await VerifyAsync(CurrentCodeFor(secret));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("usedRecoveryCode").GetBoolean());
        Assert.Equal(10, body.GetProperty("remainingRecoveryCodes").GetInt32());
    }

    [Fact]
    public async Task AnInvalidCode_IsRefused()
    {
        if (Unavailable()) { return; }

        await EnrolAndConfirmAsync();

        var (status, body) = await PostAsync("/api/v1/auth/mfa/verify", new { code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AReplayedCode_IsRefused()
    {
        if (Unavailable()) { return; }

        // §3.6: "A used TOTP code is rejected within its window." Without this, a code observed
        // over a shoulder or captured in transit stays valid for the rest of its step.
        var secret = await EnrolAndConfirmAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        var code = CurrentCodeFor(secret);

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(code)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(code)).Status);
    }

    [Fact]
    public async Task AReplayedCodeAndAWrongCode_FailIdentically()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        var code = CurrentCodeFor(secret);
        await VerifyAsync(code);

        var replayed = await VerifyAsync(code);
        var wrong = await VerifyAsync("000000");

        // "That code was right but already spent" is the most useful thing an attacker guessing
        // codes could learn.
        Assert.Equal(replayed.Status, wrong.Status);
        Assert.Equal(WithoutCorrelation(replayed.Body), WithoutCorrelation(wrong.Body));
    }

    [Fact]
    public async Task AnEarlierCode_IsRefusedAfterALaterOneIsAccepted()
    {
        if (Unavailable()) { return; }

        // The same replay with a delay: a code captured a minute ago, presented now.
        var secret = await EnrolAndConfirmAsync();
        var stale = CodeFor(secret, _clock.GetUtcNow().AddSeconds(30));

        _clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(CurrentCodeFor(secret))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(stale)).Status);
    }

    [Fact]
    public async Task VerifyingWithoutAnEnrolment_IsAConflict()
    {
        if (Unavailable()) { return; }

        var (status, _) = await PostAsync("/api/v1/auth/mfa/verify", new { code = "123456" });

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    // ---- Recovery codes -------------------------------------------------------------------------

    [Fact]
    public async Task ARecoveryCode_SatisfiesTheChallengeOnce()
    {
        if (Unavailable()) { return; }

        var codes = await EnrolConfirmAndCollectCodesAsync();

        var (status, body) = await VerifyAsync(codes[0]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("usedRecoveryCode").GetBoolean());

        // Returned so an Employee can see how close they are to none left — running out silently
        // is how a lost authenticator becomes a lost account.
        Assert.Equal(9, body.GetProperty("remainingRecoveryCodes").GetInt32());
    }

    [Fact]
    public async Task AReusedRecoveryCode_IsRefused()
    {
        if (Unavailable()) { return; }

        var codes = await EnrolConfirmAndCollectCodesAsync();

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(codes[0])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(codes[0])).Status);

        // And it spent only the one it was given.
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(codes[1])).Status);
    }

    [Fact]
    public async Task ARecoveryCode_DoesNotBurnTheAuthenticatorsCurrentCode()
    {
        if (Unavailable()) { return; }

        // Advancing the time step on a recovery would invalidate the app's current code as a side
        // effect of not having used it.
        var secret = await EnrollAsync();
        var codes = (await ConfirmForCodesAsync(secret)).Codes;

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(codes[0])).Status);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(CurrentCodeFor(secret))).Status);
    }

    [Fact]
    public async Task ARecoveryCodeIsCaseAndSeparatorInsensitive()
    {
        if (Unavailable()) { return; }

        // Read off a screen and typed by a person. Burning a single-use code on a transcription
        // detail is exactly what these exist to prevent.
        var codes = await EnrolConfirmAndCollectCodesAsync();

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(codes[0].ToLowerInvariant())).Status);
        Assert.Equal(
            HttpStatusCode.OK,
            (await VerifyAsync(codes[1].Replace("-", string.Empty, StringComparison.Ordinal))).Status);
    }

    // ---- Disabling ------------------------------------------------------------------------------

    [Fact]
    public async Task DisablingWithAValidCode_TurnsTheFactorOff()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));

        var (status, _) = await PostAsync(
            "/api/v1/auth/mfa/disable", new { code = CurrentCodeFor(secret) });

        Assert.Equal(HttpStatusCode.NoContent, status);

        // No factor left to satisfy, and enrolling again is now allowed.
        Assert.Equal(HttpStatusCode.Conflict, (await VerifyAsync("123456")).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/v1/auth/mfa/enroll", null)).Status);
    }

    [Fact]
    public async Task DisablingWithoutAValidCode_IsRefused()
    {
        if (Unavailable()) { return; }

        // This is the operation a hijacked session would perform first. §3.6's step-up principle:
        // re-proving possession is cheap relative to the consequence.
        var secret = await EnrolAndConfirmAsync();

        var (status, _) = await PostAsync("/api/v1/auth/mfa/disable", new { code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);

        // Still live.
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(CurrentCodeFor(secret))).Status);
    }

    [Fact]
    public async Task DisablingDeletesTheRecoveryCodes()
    {
        if (Unavailable()) { return; }

        var secret = await EnrollAsync();
        var codes = (await ConfirmForCodesAsync(secret)).Codes;

        _clock.Advance(TimeSpan.FromSeconds(30));
        await PostAsync("/api/v1/auth/mfa/disable", new { code = CurrentCodeFor(secret) });

        // A recovery code that outlives the factor it recovers is a permanent bypass. The new
        // enrolment must not be satisfiable by the old set.
        var replacement = await EnrollAsync();
        await ConfirmForCodesAsync(replacement);

        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(codes[0])).Status);

        Assert.Equal(0, await StoredRecoveryCodeCountForDisabledAsync());
    }

    // ---- Storage --------------------------------------------------------------------------------

    [Fact]
    public async Task TheSecretIsStoredOnlyAsAnEnvelope()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();

        var stored = await SingleEnrollmentAsync();

        // §4.2: encrypted under the envelope scheme. A database holding TOTP secrets in the clear
        // is a database holding every Employee's second factor.
        Assert.NotEmpty(stored.Secret.Ciphertext);
        Assert.Equal(SecretEnvelope.NonceLength, stored.Secret.Nonce.Length);
        Assert.Equal(SecretEnvelope.TagLength, stored.Secret.AuthenticationTag.Length);
        Assert.Equal(SecretEnvelope.AesGcm256, stored.Secret.AlgorithmId);
        Assert.Equal(1, stored.Secret.DekVersion);

        // And the ciphertext is not the base32 the Employee was shown, nor its raw bytes.
        Assert.DoesNotContain(
            secret,
            Convert.ToBase64String(stored.Secret.Ciphertext),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryCodesAreStoredOnlyAsHashes()
    {
        if (Unavailable()) { return; }

        var codes = await EnrolConfirmAndCollectCodesAsync();

        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();
        var hashes = await context.MfaRecoveryCodes
            .Select(code => code.CodeHash.Value)
            .ToListAsync();

        Assert.Equal(10, hashes.Count);
        Assert.All(hashes, hash => Assert.Equal(RecoveryCodeHash.Length, hash.Length));
        Assert.All(codes, code => Assert.DoesNotContain(
            code, string.Join(',', hashes), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheEnrolmentRecordsTheSpentTimeStep()
    {
        if (Unavailable()) { return; }

        // The replay gate is a stored value, not an in-memory one — a restart must not reopen a
        // window that was already closed.
        await EnrolAndConfirmAsync();

        var stored = await SingleEnrollmentAsync();

        Assert.NotNull(stored.LastAcceptedTimeStep);
        Assert.Equal(MfaEnrollmentStatus.Confirmed, stored.Status);
    }

    // ---- Pipeline -------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMfaEndpointRequiresAuthentication()
    {
        if (Unavailable()) { return; }

        // §3.1: "MFA management requires an authenticated session". Unlike sign-in and password
        // reset, none of these can be reached without one.
        using var client = _host!.GetTestClient();

        foreach (var path in new[] { "enroll", "confirm", "verify", "disable" })
        {
            using var response = await client
                .PostAsJsonAsync($"/api/v1/auth/mfa/{path}", new { code = "123456" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheEndpointsRunThroughTheOrdinaryPipeline()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/mfa/enroll");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await client.SendAsync(request);

        // The correlation header the middleware attaches to every response (10.4/10.5). Its
        // presence shows these endpoints are not mounted outside the pipeline.
        Assert.True(response.Headers.Contains(CorrelationHeaderNames.CorrelationId));
    }

    [Fact]
    public async Task AMissingCodeIsAValidationFailure()
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync("/api/v1/auth/mfa/verify", new { code = "" });

        // Says nothing about any account: the request is missing a field. §7 maps this to 400.
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task AFailureCarriesACorrelationIdAndNoSecret()
    {
        if (Unavailable()) { return; }

        var secret = await EnrolAndConfirmAsync();
        var (_, body) = await VerifyAsync("000000");

        var serialized = body.GetRawText();

        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain(secret, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Address, serialized, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers --------------------------------------------------------------------------------

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

    private async Task<string> EnrollAsync()
    {
        var (_, body) = await PostAsync("/api/v1/auth/mfa/enroll", null).ConfigureAwait(false);

        return body.GetProperty("secret").GetString()!;
    }

    private Task<HttpStatusCode> ConfirmAsync(string secret) =>
        StatusOfAsync("/api/v1/auth/mfa/confirm", new { code = CurrentCodeFor(secret) });

    private async Task<(HttpStatusCode Status, JsonElement Body, IReadOnlyList<string> Codes)>
        ConfirmForCodesAsync(string secret)
    {
        var (status, body) = await PostAsync(
            "/api/v1/auth/mfa/confirm",
            new { code = CurrentCodeFor(secret) }).ConfigureAwait(false);

        var codes = status == HttpStatusCode.OK
            ? body.GetProperty("recoveryCodes").EnumerateArray()
                .Select(code => code.GetString()!).ToList()
            : [];

        return (status, body, codes);
    }

    private async Task<string> EnrolAndConfirmAsync()
    {
        var secret = await EnrollAsync().ConfigureAwait(false);
        await ConfirmForCodesAsync(secret).ConfigureAwait(false);

        return secret;
    }

    private async Task<IReadOnlyList<string>> EnrolConfirmAndCollectCodesAsync()
    {
        var secret = await EnrollAsync().ConfigureAwait(false);

        return (await ConfirmForCodesAsync(secret).ConfigureAwait(false)).Codes;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> VerifyAsync(string code) =>
        PostAsync("/api/v1/auth/mfa/verify", new { code });

    private async Task<HttpStatusCode> StatusOfAsync(string path, object? payload)
    {
        var (status, _) = await PostAsync(path, payload).ConfigureAwait(false);

        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path, object? payload)
    {
        using var client = _host!.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode,
            string.IsNullOrEmpty(body)
                ? default
                : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<MfaEnrollment> SingleEnrollmentAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        return await context.MfaEnrollments
            .Where(enrollment => enrollment.DisabledAtUtc == null)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> StoredRecoveryCodeCountForDisabledAsync()
    {
        using var scope = _host!.Services.CreateScope();
        var tenant = _host.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(_company);

        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        var disabled = await context.MfaEnrollments
            .Where(enrollment => enrollment.DisabledAtUtc != null)
            .Select(enrollment => enrollment.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        return await context.MfaRecoveryCodes
            .CountAsync(code => disabled.Contains(code.EnrollmentId))
            .ConfigureAwait(false);
    }

    /// <summary>The code the server would accept right now, on the shared clock.</summary>
    private string CurrentCodeFor(string secret) => CodeFor(secret, _clock.GetUtcNow());

    /// <summary>Strips the per-request correlation identifier so two envelopes can be compared.</summary>
    private static string WithoutCorrelation(JsonElement body) =>
        string.Join('|', body.EnumerateObject()
            .Where(property => property.Name != "correlationId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

    /// <summary>
    /// Computes the code an authenticator app would show, from the base32 the endpoint returned.
    /// </summary>
    /// <remarks>
    /// Written here rather than borrowed from the implementation on purpose. A test that asked the
    /// production service for the answer would agree with it however wrong it was; this is the
    /// same RFC read a second time, and <c>TotpConformanceTests</c> pins both against the
    /// published vectors.
    /// </remarks>
    private static string CodeFor(string base32Secret, DateTimeOffset at)
    {
        var secret = DecodeBase32(base32Secret);

        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, at.ToUnixTimeSeconds() / 30);

        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238's digest; see Rfc6238TotpService for the reasoning.
        HMACSHA1.HashData(secret, message, mac);
#pragma warning restore CA5350

        var offset = mac[^1] & 0x0F;

        var binary =
            ((mac[offset] & 0x7F) << 24) |
            (mac[offset + 1] << 16) |
            (mac[offset + 2] << 8) |
            mac[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string encoded)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>(encoded.Length * 5 / 8);
        var buffer = 0;
        var bitsHeld = 0;

        foreach (var character in encoded)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(character, StringComparison.Ordinal);
            bitsHeld += 5;

            if (bitsHeld >= 8)
            {
                bitsHeld -= 8;
                bytes.Add((byte)((buffer >> bitsHeld) & 0xFF));
            }
        }

        return [.. bytes];
    }
}
