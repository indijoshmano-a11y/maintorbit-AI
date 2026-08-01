using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Validates JWT access tokens.
/// </summary>
/// <remarks>
/// SD-013 requires signature, expiry, issuer, audience, and token type all checked, with "every
/// field validated; none assumed". The parameters below turn each of those on explicitly rather
/// than relying on a default, because a default that changes between library versions changes a
/// security control silently.
/// <para>
/// <b>The algorithm is pinned.</b> Restricting to RS256 is what makes an asymmetric scheme safe:
/// without it, a token claiming <c>alg: none</c> or one signed with HMAC using the public key as
/// the shared secret would be presented for verification. Both are classic JWT forgeries, and both
/// are refused here because the algorithm is not read from the token.
/// </para>
/// <para>
/// Every failure returns the same error. Reporting "expired" separately from "bad signature" tells
/// a forger which part to fix next.
/// </para>
/// </remarks>
internal sealed class JwtAccessTokenValidator : IAccessTokenValidator
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly TokenValidationParameters _parameters;

    public JwtAccessTokenValidator(IOptions<JwtOptions> options, SigningKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyRing);

        var value = options.Value;
        _parameters = AccessTokenValidationParameters.Create(value, keyRing);
    }

    /// <inheritdoc />
    public async Task<Result<AccessTokenClaims>> ValidateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Rejected();
        }

        var result = await Handler.ValidateTokenAsync(token, _parameters).ConfigureAwait(false);

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return Rejected();
        }

        // SD-013: token type is a validated claim, not a convention. A refresh token presented
        // here must be refused — the confusion attack this exists to stop.
        if (!jwt.TryGetClaim(AccessTokenClaimNames.TokenType, out var tokenType)
            || !string.Equals(tokenType.Value, AccessTokenTypes.Access, StringComparison.Ordinal))
        {
            return Rejected();
        }

        if (!TryReadGuid(jwt, AccessTokenClaimNames.Subject, out var employeeId)
            || !TryReadGuid(jwt, AccessTokenClaimNames.CompanyId, out var companyId)
            || !TryReadGuid(jwt, AccessTokenClaimNames.SessionId, out var sessionId))
        {
            // A token that validated but is missing a required claim was signed by a trusted key,
            // so this is a defect on the issuing side rather than an attack. It is still refused:
            // a caller with no Company cannot be given a tenant context.
            return Rejected();
        }

        return Result.Success(new AccessTokenClaims(
            new EmployeeId(employeeId),
            new CompanyId(companyId),
            new SessionId(sessionId),
            jwt.IssuedAt,
            jwt.ValidTo));
    }

    private static Result<AccessTokenClaims> Rejected() =>
        Result.Failure<AccessTokenClaims>(
            Error.AuthenticationFailed("The access token is not valid."));

    private static bool TryReadGuid(JsonWebToken token, string claim, out Guid value)
    {
        value = Guid.Empty;

        return token.TryGetClaim(claim, out var found)
               && Guid.TryParseExact(found.Value, "N", out value)
               && value != Guid.Empty;
    }
}
