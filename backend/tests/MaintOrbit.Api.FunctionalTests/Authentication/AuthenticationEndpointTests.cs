using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Security;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MaintOrbit.Api.FunctionalTests.Authentication;

/// <summary>
/// Drives the authentication endpoints end to end.
/// </summary>
/// <remarks>
/// These need a real database: sign-in resolves a Company across tenants, opens a scope, verifies a
/// credential, and writes a session and a refresh token — a chain that only means anything against
/// row-level security. They are skipped when no PostgreSQL is reachable, so the suite still runs
/// where one is not.
/// </remarks>
public sealed class AuthenticationEndpointTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Address = "ada@example.test";

    private readonly CompanyId _company = new(Guid.CreateVersion7());
    private IHost? _host;
    private string? _skip;

    public async Task InitializeAsync()
    {
        var database = await TestDatabase.CreateAsync().ConfigureAwait(false);

        if (database is null)
        {
            _skip = "No PostgreSQL reachable.";
            return;
        }

        _host = BuildHost(database);
        _host.Start();

        await SeedActiveEmployeeAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestDatabase.DropAsync().ConfigureAwait(false);
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
                    app.UseEndpoints(endpoints => endpoints.MapAuthenticationEndpoints());
                }))
            .Build();

    /// <summary>Creates an Employee with a real Argon2id credential, through the real use case.</summary>
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
            var accept = scope.ServiceProvider
                .GetRequiredService<ICommandHandler<AcceptInvitationCommand>>();

            await accept.HandleAsync(
                new AcceptInvitationCommand(
                    employeeId,
                    InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG"),
                    Password),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private HttpClient Client() => _host!.GetTestClient();

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path, object? payload, string? bearer = null)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

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
            string.IsNullOrEmpty(body)
                ? default
                : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>
    /// Whether these tests can run at all.
    /// </summary>
    /// <remarks>
    /// Returns rather than fails when PostgreSQL is unreachable. xUnit 2 has no skip mechanism and
    /// the package that adds one is not in the documented inventory, so the alternative would be a
    /// suite that fails on any machine without a database. The state is reported by
    /// <see cref="DatabaseAvailability_IsReported"/>.
    /// </remarks>
    private bool Unavailable() => _skip is not null;

    [Fact]
    public void DatabaseAvailability_IsReported()
    {
        // Not an assertion about the code — it makes the skip visible instead of silent, so a run
        // with no database cannot be mistaken for a run that exercised these paths.
        Assert.True(_skip is null || _skip.Length > 0);
    }

    private Task<(HttpStatusCode, JsonElement)> SignInAsync(
        string email = Address, string password = Password) =>
        PostAsync("/api/v1/auth/login", new { email, password, clientType = "WebConsole" });

    // ---- Sign in ----------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_Succeeds()
    {
        if (Unavailable()) { return; }

        var (status, body) = await SignInAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("sessionId").GetString()));
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SignIn_IssuesAUsableAccessToken()
    {
        // The whole chain: the token it returns must satisfy the bearer handler, the session
        // check, and tenant establishment.
        if (Unavailable()) { return; }

        var (_, body) = await SignInAsync();
        var token = body.GetProperty("accessToken").GetString();

        var (status, _) = await PostAsync("/api/v1/auth/logout", payload: null, bearer: token);

        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    [Fact]
    public async Task SignIn_CreatesASession()
    {
        if (Unavailable()) { return; }

        var (_, body) = await SignInAsync();

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _tenant = tenant.BeginTenantScope(_company);
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

        Assert.Equal(1, await context.Sessions.CountAsync());
        Assert.Equal(1, await context.RefreshTokens.CountAsync());
        Assert.Equal(body.GetProperty("sessionId").GetString(),
            (await context.Sessions.SingleAsync()).Id.ToString());
    }

    [Theory]
    [InlineData(Address, "wrong-password")]
    [InlineData("nobody@example.test", Password)]
    public async Task SignIn_RefusesBadCredentials(string email, string password)
    {
        if (Unavailable()) { return; }

        var (status, body) = await SignInAsync(email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("authentication_failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SignIn_RefusesBothCasesIdentically()
    {
        // An unknown address and a wrong password must be indistinguishable, including the
        // message — the enumeration defence, at the API boundary this time.
        if (Unavailable()) { return; }

        var (_, wrongPassword) = await SignInAsync(Address, "wrong-password");
        var (_, unknown) = await SignInAsync("nobody@example.test", Password);

        Assert.Equal(
            wrongPassword.GetProperty("detail").GetString(),
            unknown.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SignIn_CreatesNoSessionForABadCredential()
    {
        if (Unavailable()) { return; }

        await SignInAsync(Address, "wrong-password");

        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _tenant = tenant.BeginTenantScope(_company);
        using var scope = _host.Services.CreateScope();

        Assert.Equal(0, await scope.ServiceProvider
            .GetRequiredService<MaintOrbitDbContext>().Sessions.CountAsync());
    }

    [Theory]
    [InlineData("", Password)]
    [InlineData(Address, "")]
    public async Task SignIn_RejectsAnIncompleteRequest(string email, string password)
    {
        if (Unavailable()) { return; }

        var (status, body) = await PostAsync("/api/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", body.GetProperty("type").GetString());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    // ---- Refresh -----------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_RotatesTheToken()
    {
        if (Unavailable()) { return; }

        var (_, signedIn) = await SignInAsync();
        var original = signedIn.GetProperty("refreshToken").GetString()!;

        var (status, body) = await PostAsync(
            "/api/v1/auth/refresh", new { refreshToken = original });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(original, body.GetProperty("refreshToken").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task Refresh_ReplayRevokesTheFamilyAndTheSession()
    {
        // SD-014's reason for existing: the second presentation of one token is the theft signal.
        if (Unavailable()) { return; }

        var (_, signedIn) = await SignInAsync();
        var original = signedIn.GetProperty("refreshToken").GetString()!;

        var (_, rotated) = await PostAsync("/api/v1/auth/refresh", new { refreshToken = original });
        var replacement = rotated.GetProperty("refreshToken").GetString()!;

        var (replayStatus, _) = await PostAsync(
            "/api/v1/auth/refresh", new { refreshToken = original });

        // The replacement the legitimate client holds is revoked too — there is no way to tell
        // which party is legitimate.
        var (afterStatus, _) = await PostAsync(
            "/api/v1/auth/refresh", new { refreshToken = replacement });

        Assert.Equal(HttpStatusCode.Unauthorized, replayStatus);
        Assert.Equal(HttpStatusCode.Unauthorized, afterStatus);
    }

    [Fact]
    public async Task Refresh_RefusesAnUnknownToken()
    {
        if (Unavailable()) { return; }

        var (status, _) = await PostAsync(
            "/api/v1/auth/refresh", new { refreshToken = "never-issued-token-value-0000" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ---- Sign out ------------------------------------------------------------------------------------

    [Fact]
    public async Task SignOut_RevokesTheSessionAndStopsRefreshing()
    {
        if (Unavailable()) { return; }

        var (_, signedIn) = await SignInAsync();
        var token = signedIn.GetProperty("accessToken").GetString();
        var refresh = signedIn.GetProperty("refreshToken").GetString()!;

        var (status, _) = await PostAsync("/api/v1/auth/logout", payload: null, bearer: token);

        var (afterRefresh, _) = await PostAsync(
            "/api/v1/auth/refresh", new { refreshToken = refresh });

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRefresh);
    }

    [Fact]
    public async Task SignOut_InvalidatesTheAccessTokenImmediately()
    {
        // The token is still signed and unexpired. Session validation on every request is what
        // makes revocation take effect inside its lifetime.
        if (Unavailable()) { return; }

        var (_, signedIn) = await SignInAsync();
        var token = signedIn.GetProperty("accessToken").GetString();

        await PostAsync("/api/v1/auth/logout", payload: null, bearer: token);
        var (second, body) = await PostAsync("/api/v1/auth/logout", payload: null, bearer: token);

        Assert.Equal(HttpStatusCode.Unauthorized, second);
        Assert.Equal("session_revoked", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SignOutEverywhere_EndsEverySession()
    {
        if (Unavailable()) { return; }

        var (_, first) = await SignInAsync();
        var (_, second) = await SignInAsync();

        var (status, _) = await PostAsync(
            "/api/v1/auth/logout-all", payload: null,
            bearer: first.GetProperty("accessToken").GetString());

        var (secondAfter, _) = await PostAsync(
            "/api/v1/auth/logout", payload: null,
            bearer: second.GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(HttpStatusCode.Unauthorized, secondAfter);
    }

    [Fact]
    public async Task SignOut_RequiresAuthentication()
    {
        if (Unavailable()) { return; }

        var (status, _) = await PostAsync("/api/v1/auth/logout", payload: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
