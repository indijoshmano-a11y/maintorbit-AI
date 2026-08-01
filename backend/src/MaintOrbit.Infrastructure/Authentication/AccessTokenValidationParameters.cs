using MaintOrbit.Application.Abstractions.Security;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// The one definition of how an access token is validated.
/// </summary>
/// <remarks>
/// Shared by <see cref="JwtAccessTokenValidator"/> and by the ASP.NET Core bearer handler. Two
/// independent sets of parameters would be two security controls that could drift — and the
/// dangerous direction of drift is silent: the middleware accepting something the validator would
/// refuse produces no error anywhere.
/// <para>
/// Every check is turned on explicitly rather than left to a default, because a default that
/// changes between library versions changes a security control without a diff.
/// </para>
/// </remarks>
internal static class AccessTokenValidationParameters
{
    public static TokenValidationParameters Create(JwtOptions options, SigningKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyRing);

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,

            ValidateAudience = true,
            ValidAudience = options.Audience,

            ValidateLifetime = true,
            RequireExpirationTime = true,

            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = keyRing.ValidationKeys,

            // Only RS256. The single line that refuses alg:none and an HMAC token forged with the
            // public key as its secret — both are ordinary-looking tokens until something declines
            // to read the algorithm from the token it is checking.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

            // No skew. The library default of five minutes on a fifteen-minute token grants a
            // third of its life again after expiry, and SD-013 bounds a stolen token by that
            // lifetime.
            ClockSkew = TimeSpan.Zero,

            // The claim carrying the Employee. Without this the handler maps `sub` to a
            // ClaimsIdentity name claim under a legacy URI, and reading it back becomes guesswork.
            NameClaimType = AccessTokenClaimNames.Subject
        };
    }
}
