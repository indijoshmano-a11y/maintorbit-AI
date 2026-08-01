using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Supplies the token validation parameters to the API host's bearer handler.
/// </summary>
/// <remarks>
/// Exists so the host can configure framework authentication without reaching into this assembly.
/// The signing key ring stays internal — it holds private key material, and the number of places
/// able to touch that should be as small as the design allows.
/// <para>
/// It matters that this is the <i>same</i> definition <c>IAccessTokenValidator</c> uses. Two
/// independent parameter sets would be two security controls that could drift, and the dangerous
/// direction is silent: the middleware accepting a token the validator would refuse produces no
/// error anywhere.
/// </para>
/// </remarks>
public interface IAccessTokenValidationParametersFactory
{
    /// <summary>Builds the parameters every access token is validated against.</summary>
    TokenValidationParameters Create();
}

/// <inheritdoc />
internal sealed class AccessTokenValidationParametersFactory(
    Microsoft.Extensions.Options.IOptions<JwtOptions> options,
    SigningKeyRing keyRing)
    : IAccessTokenValidationParametersFactory
{
    /// <inheritdoc />
    public TokenValidationParameters Create() =>
        AccessTokenValidationParameters.Create(options.Value, keyRing);
}
