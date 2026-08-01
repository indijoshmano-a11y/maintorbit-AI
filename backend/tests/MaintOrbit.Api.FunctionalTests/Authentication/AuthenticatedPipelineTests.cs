using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Api.HealthChecks;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Api.FunctionalTests.Authentication;

/// <summary>
/// Drives real access tokens through the real pipeline.
/// </summary>
/// <remarks>
/// Every assertion runs against <c>UseApiPipeline</c> — the actual ordering, with the actual bearer
/// handler — because what is being tested is whether authentication, session validation, and tenant
/// establishment compose. Each of them works in isolation already.
/// <para>
/// Session validation is stubbed rather than backed by a database: what varies between these cases
/// is the <i>decision</i>, and a decision is not made more real by the row it came from. The
/// repository's own behaviour under row-level security is covered against PostgreSQL separately.
/// </para>
/// </remarks>
public sealed class AuthenticatedPipelineTests
{
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly SessionId Session = SessionId.New();

    private const string ProtectedPath = "/whoami";

    private sealed class Host : IDisposable
    {
        private readonly IHost _host;

        public StubSessionValidator Sessions { get; } = new();

        public Host()
        {
            _host = new HostBuilder()
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
                                ["Persistence:ConnectionString"] =
                                    "Host=localhost;Database=maintorbit_test;Username=maintorbit"
                            }))
                            .Build();

                        services.AddSingleton<IConfiguration>(configuration);
                        services
                            .AddApplication()
                            .AddInfrastructure(configuration)
                            .AddApi(configuration)
                            .AddObservability(configuration);

                        // Replaces the database-backed validator; the decision under test is the
                        // pipeline's, not the repository's.
                        services.RemoveAll<ISessionValidator>();
                        services.AddSingleton<ISessionValidator>(Sessions);
                    })
                    .Configure(app =>
                    {
                        app.UseApiPipeline();

                        // RequireAuthorization uses the built-in "an authenticated user is
                        // required" policy. It is not a permission policy — those are a later
                        // milestone — but without it the bearer handler never challenges, because
                        // nothing has asked it to.
                        app.UseEndpoints(endpoints =>
                            endpoints.MapGet(ProtectedPath, static (HttpContext http) =>
                            {
                                var identity = http.RequestServices
                                    .GetRequiredService<ICurrentIdentity>();
                                var tenant = http.RequestServices
                                    .GetRequiredService<ITenantContext>();

                                return Results.Ok(new
                                {
                                    authenticated = identity.IsAuthenticated,
                                    employee = identity.EmployeeId?.ToString(),
                                    company = identity.CompanyId?.ToString(),
                                    session = identity.SessionId?.ToString(),
                                    tenant = tenant.Current?.ToString()
                                });
                            }).RequireAuthorization());

                        app.UseEndpoints(endpoints => endpoints.MapHealthEndpoints());
                    }))
                .Build();

            _host.Start();
        }

        public HttpClient Client() => _host.GetTestClient();

        public IAccessTokenGenerator Tokens =>
            _host.Services.GetRequiredService<IAccessTokenGenerator>();

        public void Dispose() => _host.Dispose();
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        Host host, string? token)
    {
        using var client = host.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedPath);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---- The happy path ---------------------------------------------------------------------------

    [Fact]
    public async Task AValidToken_IsAccepted()
    {
        using var host = new Host();
        var token = host.Tokens.Generate(Employee, Company, Session);

        var (status, _) = await GetAsync(host, token.Value);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task AValidToken_ResolvesEmployeeCompanyAndSession()
    {
        using var host = new Host();
        var token = host.Tokens.Generate(Employee, Company, Session);

        var (_, body) = await GetAsync(host, token.Value);
        var json = JsonDocument.Parse(body).RootElement;

        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(Employee.ToString(), json.GetProperty("employee").GetString());
        Assert.Equal(Company.ToString(), json.GetProperty("company").GetString());
        Assert.Equal(Session.ToString(), json.GetProperty("session").GetString());
    }

    [Fact]
    public async Task AValidToken_EstablishesTheTenantContext()
    {
        // TC-1: derived server-side from the credential. Without this every downstream query would
        // return zero rows under row-level security.
        using var host = new Host();
        var token = host.Tokens.Generate(Employee, Company, Session);

        var (_, body) = await GetAsync(host, token.Value);

        Assert.Equal(
            Company.ToString(),
            JsonDocument.Parse(body).RootElement.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task TheSession_IsValidatedOnEveryRequest()
    {
        // A signed token proves what was true when it was issued. Checking once and trusting the
        // token thereafter would make revocation meaningless for its whole lifetime.
        using var host = new Host();
        var token = host.Tokens.Generate(Employee, Company, Session);

        await GetAsync(host, token.Value);
        await GetAsync(host, token.Value);
        await GetAsync(host, token.Value);

        Assert.Equal(3, host.Sessions.Calls);
    }

    [Fact]
    public async Task TheSessionCheck_ReceivesTheTokensOwnClaims()
    {
        // The session is validated *against* the token, so a token naming someone else's session
        // is caught rather than merely looked up.
        using var host = new Host();
        var token = host.Tokens.Generate(Employee, Company, Session);

        await GetAsync(host, token.Value);

        Assert.Equal((Session, Employee, Company), host.Sessions.LastCall);
    }

    // ---- Token failures ----------------------------------------------------------------------------

    [Fact]
    public async Task NoToken_IsRefused()
    {
        using var host = new Host();

        var (status, _) = await GetAsync(host, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task AMalformedToken_IsRefused(string token)
    {
        using var host = new Host();

        var (status, _) = await GetAsync(host, token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ATokenSignedByAnotherKey_IsRefused()
    {
        using var host = new Host();
        using var foreign = System.Security.Cryptography.RSA.Create(2048);

        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://api.maintorbit.test",
            Audience = "maintorbit-api",
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(foreign), SecurityAlgorithms.RsaSha256),
            Claims = Claims()
        });

        var (status, _) = await GetAsync(host, forged);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Theory]
    [InlineData("https://someone-else.test", "maintorbit-api")]
    [InlineData("https://api.maintorbit.test", "another-api")]
    public async Task AWrongIssuerOrAudience_IsRefused(string issuer, string audience)
    {
        using var host = new Host();
        var forged = SignedByTheHost(issuer, audience, DateTime.UtcNow.AddMinutes(10), Claims());

        var (status, _) = await GetAsync(host, forged);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        using var host = new Host();
        var expired = SignedByTheHost(
            "https://api.maintorbit.test", "maintorbit-api",
            DateTime.UtcNow.AddMinutes(-1), Claims());

        var (status, _) = await GetAsync(host, expired);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AnUnsignedToken_IsRefused()
    {
        // alg:none — refused because the pipeline pins RS256 rather than reading the algorithm
        // from the token it is checking.
        using var host = new Host();

        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "https://api.maintorbit.test",
            ["aud"] = "maintorbit-api",
            ["sub"] = Employee.Value.ToString("n"),
            ["company_id"] = Company.Value.ToString("n"),
            ["sid"] = Session.Value.ToString("n"),
            ["token_type"] = "access",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        }));

        var (status, _) = await GetAsync(host, $"{header}.{payload}.");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ARefreshTokenPresentedAsAnAccessToken_IsRefused()
    {
        // SD-013: token type is a validated claim. Signed by the real key and valid in every other
        // respect — only the type differs.
        using var host = new Host();

        var claims = Claims();
        claims["token_type"] = "refresh";

        var refresh = SignedByTheHost(
            "https://api.maintorbit.test", "maintorbit-api",
            DateTime.UtcNow.AddMinutes(10), claims);

        var (status, _) = await GetAsync(host, refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ATokenMissingTheCompanyClaim_IsRefused()
    {
        // Signed by a trusted key, so an issuer-side defect rather than an attack — and still
        // refused, because a request with no Company cannot be given a tenant context.
        using var host = new Host();

        var claims = Claims();
        claims.Remove("company_id");

        var untenanted = SignedByTheHost(
            "https://api.maintorbit.test", "maintorbit-api",
            DateTime.UtcNow.AddMinutes(10), claims);

        var (status, _) = await GetAsync(host, untenanted);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ATokenFailure_ReachesNoSessionLookup()
    {
        // Cheap checks first: an invalid token must not cost a database round trip.
        using var host = new Host();

        await GetAsync(host, "not-a-token");

        Assert.Equal(0, host.Sessions.Calls);
    }

    // ---- Session failures ---------------------------------------------------------------------------

    [Fact]
    public async Task ARevokedSession_IsRefused()
    {
        using var host = new Host();
        host.Sessions.Reject();
        var token = host.Tokens.Generate(Employee, Company, Session);

        var (status, body) = await GetAsync(host, token.Value);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "session_revoked",
            JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ARevokedSession_StopsTheRequestReachingTheEndpoint()
    {
        using var host = new Host();
        host.Sessions.Reject();
        var token = host.Tokens.Generate(Employee, Company, Session);

        var (_, body) = await GetAsync(host, token.Value);

        Assert.DoesNotContain("authenticated", body, StringComparison.Ordinal);
    }

    // ---- What a refusal reveals -----------------------------------------------------------------------

    [Fact]
    public async Task ARefusal_LeaksNoReasonInTheChallengeHeader()
    {
        // The default challenge emits error="invalid_token" with an error_description naming the
        // failure and, for an expired token, the exact expiry of somebody else's session.
        using var host = new Host();
        using var client = host.Client();

        var expired = SignedByTheHost(
            "https://api.maintorbit.test", "maintorbit-api",
            DateTime.UtcNow.AddMinutes(-1), Claims());

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        using var response = await client.SendAsync(request);

        var challenge = string.Join(' ', response.Headers.WwwAuthenticate.Select(static h => h.ToString()));

        Assert.Equal("Bearer", challenge);
        Assert.DoesNotContain("error", challenge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", challenge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusal_ReturnsTheDocumentedEnvelope()
    {
        using var host = new Host();

        var (_, body) = await GetAsync(host, "not-a-token");
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal("authentication_failed", json.GetProperty("type").GetString());
        Assert.Equal(401, json.GetProperty("status").GetInt32());
        Assert.False(json.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task ARefusal_CarriesTheCorrelationIdentifier()
    {
        // The pipeline order holds: correlation runs before authentication, so a 401 is still
        // traceable.
        using var host = new Host();
        using var client = host.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedPath);
        request.Headers.Add(CorrelationHeaderNames.CorrelationId, "auth-trace-1");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("auth-trace-1", Assert.Single(
            response.Headers.GetValues(CorrelationHeaderNames.CorrelationId)));
        Assert.Equal(
            "auth-trace-1",
            JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString());
    }

    // ---- Unauthenticated paths still work ----------------------------------------------------------------

    [Fact]
    public async Task HealthEndpointsRemainReachable()
    {
        // Authentication is registered but nothing requires it yet, so an unauthenticated request
        // proceeds with no tenant — under which row-level security shows it nothing.
        using var host = new Host();
        using var client = host.Client();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, host.Sessions.Calls);
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_OpensNoTenantScope()
    {
        using var host = new Host();

        var (_, body) = await GetAsync(host, token: null);

        Assert.DoesNotContain(Company.ToString(), body, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static Dictionary<string, object> Claims() => new(StringComparer.Ordinal)
    {
        ["sub"] = Employee.Value.ToString("n"),
        ["company_id"] = Company.Value.ToString("n"),
        ["sid"] = Session.Value.ToString("n"),
        ["token_type"] = "access"
    };

    /// <summary>
    /// Signs a token with the same key the host trusts, so only the field under test differs.
    /// </summary>
    /// <remarks>
    /// Imported from the shared PEM rather than taken from the host's key ring: that ring is
    /// disposed with its container, and a test that outlived one would sign with a disposed key.
    /// </remarks>
    private static string SignedByTheHost(
        string issuer, string audience, DateTime expires, Dictionary<string, object> claims)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(TestJwtConfiguration.SigningKeyPem);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256),
            Claims = claims
        });
    }

    private sealed class StubSessionValidator : ISessionValidator
    {
        private bool _reject;

        public int Calls { get; private set; }

        public (SessionId, EmployeeId, CompanyId)? LastCall { get; private set; }

        public void Reject() => _reject = true;

        public Task<Result> ValidateAsync(
            SessionId sessionId, EmployeeId employeeId, CompanyId companyId,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastCall = (sessionId, employeeId, companyId);

            return Task.FromResult(_reject
                ? Result.Failure(new Error("session_revoked", "The session is no longer valid."))
                : Result.Success());
        }
    }
}
