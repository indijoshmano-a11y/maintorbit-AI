using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Covers access token issuance and validation.
/// </summary>
/// <remarks>
/// The forgery cases carry the most weight. An asymmetric scheme is only as strong as its refusal
/// to take the algorithm from the token: <c>alg: none</c> and HMAC-with-the-public-key are the two
/// classic JWT forgeries, and both look like ordinary tokens until something checks.
/// </remarks>
public sealed class AccessTokenTests : IDisposable
{
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly SessionId Session = SessionId.New();
    /// <summary>
    /// Issue time for generated tokens.
    /// </summary>
    /// <remarks>
    /// Anchored to the present rather than a fixed literal: the generator takes an injected clock
    /// but the validator reads real time, so a token stamped with a hard-coded date is expired
    /// before it is checked. Truncated to a whole second because the <c>exp</c> claim is unix
    /// seconds, and a sub-second issue time would not survive the round trip.
    /// </remarks>
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero);

    private const string Issuer = "https://api.maintorbit.test";
    private const string Audience = "maintorbit-api";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly List<SigningKeyRing> _rings = [];

    private JwtOptions Options(int lifetimeMinutes = 15) => new()
    {
        Issuer = Issuer,
        Audience = Audience,
        AccessTokenLifetimeMinutes = lifetimeMinutes,
        SigningKey = new JwtSigningKeyOptions
        {
            KeyId = "key-1",
            PrivateKeyPem = _key.ExportRSAPrivateKeyPem()
        }
    };

    private SigningKeyRing Ring(JwtOptions options)
    {
        var ring = new SigningKeyRing(Microsoft.Extensions.Options.Options.Create(options));
        _rings.Add(ring);
        return ring;
    }

    private JwtAccessTokenGenerator Generator(JwtOptions? options = null, DateTimeOffset? now = null)
    {
        options ??= Options();
        return new JwtAccessTokenGenerator(
            Microsoft.Extensions.Options.Options.Create(options),
            Ring(options),
            new FixedClock(now ?? Now));
    }

    private JwtAccessTokenValidator Validator(JwtOptions? options = null)
    {
        options ??= Options();
        return new JwtAccessTokenValidator(
            Microsoft.Extensions.Options.Options.Create(options), Ring(options));
    }

    private static JsonWebToken Decode(AccessToken token) => new(token.Value);

    // ---- Generation ---------------------------------------------------------------------------

    [Fact]
    public void Generate_ProducesAThreePartJwt()
    {
        var token = Generator().Generate(Employee, Company, Session);

        Assert.Equal(3, token.Value.Split('.').Length);
    }

    [Fact]
    public void Generate_CarriesExactlyTheDocumentedClaims()
    {
        // SD-013: Employee, Company, session, issued-at, expiry, token type — plus the issuer and
        // audience that validation requires. Asserted as a set, so an extra claim fails here.
        var jwt = Decode(Generator().Generate(Employee, Company, Session));

        var names = jwt.Claims.Select(static c => c.Type).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["aud", "company_id", "exp", "iat", "iss", "nbf", "sid", "sub", "token_type"],
            names);
    }

    [Fact]
    public void Generate_IdentifiesTheEmployeeCompanyAndSession()
    {
        var jwt = Decode(Generator().Generate(Employee, Company, Session));

        Assert.Equal(Employee.Value.ToString("n"), jwt.Subject);
        Assert.Equal(Company.Value.ToString("n"), jwt.GetClaim("company_id").Value);
        Assert.Equal(Session.Value.ToString("n"), jwt.GetClaim("sid").Value);
    }

    [Fact]
    public void Generate_StampsTheTokenType()
    {
        Assert.Equal(
            AccessTokenTypes.Access,
            Decode(Generator().Generate(Employee, Company, Session)).GetClaim("token_type").Value);
    }

    [Fact]
    public void Generate_ExpiresFifteenMinutesAfterIssue()
    {
        var token = Generator().Generate(Employee, Company, Session);

        Assert.Equal(Now.AddMinutes(15), token.ExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(15).UtcDateTime, Decode(token).ValidTo);
    }

    [Fact]
    public void Generate_SignsWithRs256AndNamesTheKey()
    {
        // The kid is what lets a validator pick the right key during rotation instead of trying
        // each one.
        var jwt = Decode(Generator().Generate(Employee, Company, Session));

        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Alg);
        Assert.Equal("key-1", jwt.Kid);
    }

    [Fact]
    public void Generate_RejectsAnIncompleteIdentity()
    {
        var generator = Generator();

        Assert.Throws<ArgumentException>(() => generator.Generate(EmployeeId.Empty, Company, Session));
        Assert.Throws<ArgumentException>(() => generator.Generate(Employee, CompanyId.Empty, Session));
        Assert.Throws<ArgumentException>(() => generator.Generate(Employee, Company, SessionId.Empty));
    }

    // ---- The claims that must never appear -------------------------------------------------------

    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    [InlineData("permission")]
    [InlineData("permissions")]
    [InlineData("scope")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
    public void Generate_EmitsNoAuthorizationClaim(string forbidden)
    {
        // FR-PERM-005 requires a role change effective within 60 seconds, which a self-contained
        // 15-minute token cannot honour. A token carrying permissions is a stale authorization
        // decision travelling around the network.
        var jwt = Decode(Generator().Generate(Employee, Company, Session));

        Assert.DoesNotContain(jwt.Claims, c =>
            string.Equals(c.Type, forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Validation ------------------------------------------------------------------------------

    [Fact]
    public async Task Validate_AcceptsAFreshToken()
    {
        var options = Options();
        var token = Generator(options).Generate(Employee, Company, Session);

        var result = await Validator(options).ValidateAsync(token.Value, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Validate_ReturnsTheIdentityFromTheToken()
    {
        var options = Options();
        var token = Generator(options).Generate(Employee, Company, Session);

        var claims = (await Validator(options).ValidateAsync(token.Value, CancellationToken.None)).Value;

        Assert.Equal(Employee, claims.EmployeeId);
        Assert.Equal(Company, claims.CompanyId);
        Assert.Equal(Session, claims.SessionId);
    }

    [Fact]
    public async Task Validate_RejectsAnExpiredToken()
    {
        var options = Options();
        // Issued sixteen minutes ago, so a fifteen-minute token has expired.
        var token = Generator(options, Now.AddMinutes(-16)).Generate(Employee, Company, Session);

        var result = await Validator(options).ValidateAsync(token.Value, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Fact]
    public async Task Validate_RejectsATokenThatExpiredMomentsAgo()
    {
        // ClockSkew is zero. The library default of five minutes would grant a third of a
        // fifteen-minute token's life again after expiry, which is exactly the window SD-013
        // bounds a stolen token by.
        var options = Options(lifetimeMinutes: 1);
        var token = Generator(options, DateTimeOffset.UtcNow.AddMinutes(-2))
            .Generate(Employee, Company, Session);

        var result = await Validator(options).ValidateAsync(token.Value, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsATokenSignedByAnotherKey()
    {
        var issued = Options();
        var token = Generator(issued).Generate(Employee, Company, Session);

        var foreign = new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = new JwtSigningKeyOptions
            {
                KeyId = "key-1",
                PrivateKeyPem = _otherKey.ExportRSAPrivateKeyPem()
            }
        };

        var result = await Validator(foreign).ValidateAsync(token.Value, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsATamperedPayload()
    {
        var options = Options();
        var token = Generator(options).Generate(Employee, Company, Session);

        var parts = token.Value.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Base64UrlEncoder.Decode(parts[1]))!;

        // Swap the Company — the single most valuable claim to forge.
        payload["company_id"] = JsonSerializer.SerializeToElement(Guid.CreateVersion7().ToString("n"));

        var forged = string.Join('.',
            parts[0],
            Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload)),
            parts[2]);

        var result = await Validator(options).ValidateAsync(forged, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsTheWrongIssuer()
    {
        var options = Options();
        var token = Generator(options).Generate(Employee, Company, Session);

        var elsewhere = new JwtOptions
        {
            Issuer = "https://someone-else.test",
            Audience = Audience,
            SigningKey = options.SigningKey
        };

        Assert.True((await Validator(elsewhere)
            .ValidateAsync(token.Value, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsTheWrongAudience()
    {
        var options = Options();
        var token = Generator(options).Generate(Employee, Company, Session);

        var elsewhere = new JwtOptions
        {
            Issuer = Issuer,
            Audience = "some-other-api",
            SigningKey = options.SigningKey
        };

        Assert.True((await Validator(elsewhere)
            .ValidateAsync(token.Value, CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task Validate_RejectsMalformedInput(string token)
    {
        Assert.True((await Validator().ValidateAsync(token, CancellationToken.None)).IsFailure);
    }

    // ---- Forgeries ---------------------------------------------------------------------------------

    [Fact]
    public async Task Validate_RejectsAnUnsignedToken()
    {
        // alg:none. The oldest JWT forgery there is, and it works against any implementation that
        // reads the algorithm from the token it is checking.
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = Employee.Value.ToString("n"),
            ["company_id"] = Company.Value.ToString("n"),
            ["sid"] = Session.Value.ToString("n"),
            ["token_type"] = AccessTokenTypes.Access,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        }));

        var result = await Validator().ValidateAsync($"{header}.{payload}.", CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsAnHmacTokenForgedWithThePublicKey()
    {
        // Algorithm confusion. The public key is not secret, so if the validator accepted HS256 an
        // attacker could sign a token with it as the HMAC secret and be believed. Pinning
        // ValidAlgorithms to RS256 is what refuses this.
        var options = Options();
        var publicKey = Encoding.UTF8.GetBytes(_key.ExportRSAPublicKeyPem());

        var handler = new JsonWebTokenHandler();
        var forged = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(publicKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = Employee.Value.ToString("n"),
                ["company_id"] = Company.Value.ToString("n"),
                ["sid"] = Session.Value.ToString("n"),
                ["token_type"] = AccessTokenTypes.Access
            }
        });

        Assert.True((await Validator(options).ValidateAsync(forged, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsATokenOfTheWrongType()
    {
        // SD-013: "a refresh token presented as an access token must be rejected". Signed by the
        // real key, valid in every other respect — only the type claim differs.
        var options = Options();
        var handler = new JsonWebTokenHandler();

        var refresh = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = Ring(options).SigningCredentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = Employee.Value.ToString("n"),
                ["company_id"] = Company.Value.ToString("n"),
                ["sid"] = Session.Value.ToString("n"),
                ["token_type"] = "refresh"
            }
        });

        Assert.True((await Validator(options).ValidateAsync(refresh, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsATokenWithNoTypeClaim()
    {
        var options = Options();
        var handler = new JsonWebTokenHandler();

        var untyped = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = Ring(options).SigningCredentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = Employee.Value.ToString("n"),
                ["company_id"] = Company.Value.ToString("n"),
                ["sid"] = Session.Value.ToString("n")
            }
        });

        Assert.True((await Validator(options).ValidateAsync(untyped, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Validate_RejectsATokenMissingTheCompany()
    {
        // Signed by a trusted key, so this is an issuer-side defect rather than an attack. Still
        // refused: a caller with no Company cannot be given a tenant context.
        var options = Options();
        var handler = new JsonWebTokenHandler();

        var untenanted = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = Ring(options).SigningCredentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = Employee.Value.ToString("n"),
                ["sid"] = Session.Value.ToString("n"),
                ["token_type"] = AccessTokenTypes.Access
            }
        });

        Assert.True((await Validator(options).ValidateAsync(untenanted, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task EveryRejection_CarriesTheSameError()
    {
        // Distinguishing "expired" from "bad signature" from "wrong audience" tells a forger which
        // part to fix next.
        var options = Options();
        var expired = Generator(options, Now.AddMinutes(-16)).Generate(Employee, Company, Session);

        var fromExpired = await Validator(options).ValidateAsync(expired.Value, CancellationToken.None);
        var fromGarbage = await Validator(options).ValidateAsync("not-a-token", CancellationToken.None);

        Assert.Equal(fromExpired.Error, fromGarbage.Error);
    }

    // ---- Key rotation --------------------------------------------------------------------------------

    [Fact]
    public async Task ATokenSignedByARetiredKey_IsStillAccepted()
    {
        // §18: rotation uses overlapping validity via key identifiers, "so it does not require a
        // flag day". Without this, promoting a new key would invalidate every live token at once.
        var before = Options();
        var token = Generator(before).Generate(Employee, Company, Session);

        var after = new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = new JwtSigningKeyOptions
            {
                KeyId = "key-2",
                PrivateKeyPem = _otherKey.ExportRSAPrivateKeyPem()
            },
            PreviousKeys =
            [
                new JwtValidationKeyOptions
                {
                    KeyId = "key-1",
                    PublicKeyPem = _key.ExportRSAPublicKeyPem()
                }
            ]
        };

        Assert.True((await Validator(after)
            .ValidateAsync(token.Value, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public void AfterRotation_NewTokensUseTheNewKey()
    {
        var after = new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = new JwtSigningKeyOptions
            {
                KeyId = "key-2",
                PrivateKeyPem = _otherKey.ExportRSAPrivateKeyPem()
            },
            PreviousKeys =
            [
                new JwtValidationKeyOptions { KeyId = "key-1", PublicKeyPem = _key.ExportRSAPublicKeyPem() }
            ]
        };

        Assert.Equal("key-2", Decode(Generator(after).Generate(Employee, Company, Session)).Kid);
    }

    // ---- Secrets ---------------------------------------------------------------------------------------

    [Fact]
    public void TheIssuedToken_DoesNotPrintItself()
    {
        // A bearer credential: whoever holds it is the Employee until it expires.
        var token = Generator().Generate(Employee, Company, Session);

        Assert.DoesNotContain("eyJ", $"{token}", StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", token.ToString());
    }

    public void Dispose()
    {
        foreach (var ring in _rings)
        {
            ring.Dispose();
        }

        _key.Dispose();
        _otherKey.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
