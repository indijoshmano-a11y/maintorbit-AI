using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Issues RS256-signed JWT access tokens.
/// </summary>
/// <remarks>
/// The claim set is exactly SD-013's: Employee, Company, session, issued-at, expiry, token type,
/// plus the issuer and audience that validation requires. Nothing else is added — and the
/// omissions are the point, because a claim that exists is a claim something eventually trusts.
/// <para>
/// <b>No roles, no permissions.</b> FR-PERM-005 requires a role change to take effect within 60
/// seconds and a 15-minute self-contained token cannot honour that, so authorization is resolved
/// server-side per request. A test asserts no such claim is ever emitted.
/// </para>
/// </remarks>
internal sealed class JwtAccessTokenGenerator(
    IOptions<JwtOptions> options,
    SigningKeyRing keyRing,
    TimeProvider timeProvider)
    : IAccessTokenGenerator
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <inheritdoc />
    public AccessToken Generate(EmployeeId employeeId, CompanyId companyId, SessionId sessionId)
    {
        if (employeeId.IsEmpty || companyId.IsEmpty || sessionId.IsEmpty)
        {
            // A token missing any of the three identifies nobody, belongs to no tenant, or cannot
            // be revoked. None is a caller error worth a result type — it is a defect in whatever
            // built the request.
            throw new ArgumentException(
                "An access token requires an Employee, a Company, and a session.");
        }

        var value = options.Value;

        // Read once, so issued-at and expiry cannot straddle a tick and produce a token that is
        // valid for a fraction less than the configured lifetime.
        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(value.AccessTokenLifetimeMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = value.Issuer,
            Audience = value.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = keyRing.SigningCredentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [AccessTokenClaimNames.Subject] = employeeId.Value.ToString("n"),
                [AccessTokenClaimNames.CompanyId] = companyId.Value.ToString("n"),
                [AccessTokenClaimNames.SessionId] = sessionId.Value.ToString("n"),
                [AccessTokenClaimNames.TokenType] = AccessTokenTypes.Access
            }
        };

        return new AccessToken(Handler.CreateToken(descriptor), expiresAt);
    }
}
