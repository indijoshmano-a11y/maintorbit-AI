using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Access token issuance and validation settings.
/// </summary>
/// <remarks>
/// The signing key arrives through configuration, which in every real deployment means the
/// environment. security-architecture §17 is explicit that the JWT signing key lives with the
/// custodian and <b>never in source or images</b>; supplying it as configuration satisfies that
/// today and is the same shape a custodian-backed provider will fill once D-6 selects one.
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// The issuer this deployment stamps into tokens and requires when validating.
    /// </summary>
    /// <remarks>
    /// Validated on every token (SD-013). Without it, a token minted by any other deployment
    /// holding a trusted key would be accepted here.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(256, MinimumLength = 1)]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    /// The audience this deployment stamps into tokens and requires when validating.
    /// </summary>
    /// <remarks>
    /// Separates tokens intended for this API from tokens intended for anything else the same
    /// issuer signs for — the Gateway, a future service identity — so one cannot be replayed
    /// against the other.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(256, MinimumLength = 1)]
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// How long an issued access token remains valid.
    /// </summary>
    /// <remarks>
    /// SD-013 fixes 15 minutes, and the range caps there rather than defaulting there: a shorter
    /// lifetime is strictly safer and a longer one contradicts the decision. The lower bound of one
    /// minute keeps a misconfiguration from producing tokens that expire before they arrive.
    /// </remarks>
    [Range(1, 15)]
    public int AccessTokenLifetimeMinutes { get; init; } = 15;

    /// <summary>The key currently used to sign.</summary>
    [Required]
    public JwtSigningKeyOptions SigningKey { get; init; } = new();

    /// <summary>
    /// Keys that are no longer used to sign but whose tokens are still accepted.
    /// </summary>
    /// <remarks>
    /// security-architecture §18: "signing key rotation uses overlapping validity via key
    /// identifiers, so it does not require a flag day". A rotation promotes a new key to
    /// <see cref="SigningKey"/> and leaves the old one here until every token it signed has
    /// expired — fifteen minutes later. Without this, rotation would invalidate every live token
    /// at once.
    /// <para>
    /// Only the public half is needed to validate, so a retired key's private material can be
    /// destroyed at rotation rather than kept around.
    /// </para>
    /// </remarks>
    public IReadOnlyList<JwtValidationKeyOptions> PreviousKeys { get; init; } = [];
}

/// <summary>The key used to sign newly issued tokens.</summary>
public sealed class JwtSigningKeyOptions
{
    /// <summary>
    /// Identifies this key in the token header.
    /// </summary>
    /// <remarks>
    /// Written as <c>kid</c> so a validator can select the right key rather than trying each.
    /// This is what makes overlapping validity work during rotation.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string KeyId { get; init; } = string.Empty;

    /// <summary>
    /// PEM-encoded RSA private key.
    /// </summary>
    /// <remarks>
    /// C4 material — compromise allows minting valid tokens for any Employee of any Company, and
    /// threat S-5 notes forged tokens bypass tombstone revocation entirely. Supplied by the
    /// environment, never committed.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string PrivateKeyPem { get; init; } = string.Empty;
}

/// <summary>A key that is accepted when validating but no longer used to sign.</summary>
public sealed class JwtValidationKeyOptions
{
    /// <summary>Identifies this key in the token header.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string KeyId { get; init; } = string.Empty;

    /// <summary>PEM-encoded RSA public key. Not secret.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicKeyPem { get; init; } = string.Empty;
}
