using MaintOrbit.Domain.Common.Results;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Validates access tokens.
/// </summary>
/// <remarks>
/// A presented token that fails validation is an expected outcome, not an exceptional one — it is
/// what an expired session, a tampered token, or a refresh token on the wrong path all look like.
/// EX-1 puts those in the return type, so a caller cannot overlook the failure path.
/// <para>
/// Every failure returns the same error for the same reason it does at login: distinguishing
/// "expired" from "wrong signature" from "wrong audience" tells an attacker which part of a
/// forged token to fix next.
/// </para>
/// </remarks>
public interface IAccessTokenValidator
{
    /// <summary>
    /// Validates signature, expiry, issuer, audience, and token type.
    /// </summary>
    Task<Result<AccessTokenClaims>> ValidateAsync(string token, CancellationToken cancellationToken);
}
