using System.Security.Cryptography;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// Supplies a throwaway signing key so composed test hosts can start.
/// </summary>
/// <remarks>
/// The JWT settings are validated at startup and have no default, so every host that composes the
/// infrastructure layer must supply them — which is the intended behaviour: a deployment that
/// cannot sign a token should refuse to start rather than fail on the first authenticated request.
/// <para>
/// The key is <b>generated, never committed</b>. security-architecture §17 requires the signing
/// key to live with the custodian and never appear in source or images, and a key checked into a
/// test fixture is a key in source — it would be found, and it would eventually be copied into a
/// deployment because it was convenient.
/// </para>
/// <para>
/// Generated once for the assembly and reused. RSA key generation is expensive enough that doing
/// it per host would dominate the runtime of every suite that starts one.
/// </para>
/// </remarks>
internal static class TestJwtConfiguration
{
    private static readonly Lazy<string> PrivateKeyPem = new(
        static () =>
        {
            using var rsa = RSA.Create(2048);
            return rsa.ExportRSAPrivateKeyPem();
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Configuration entries a test host needs to satisfy JWT startup validation.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new("Jwt:Issuer", "https://api.maintorbit.test"),
        new("Jwt:Audience", "maintorbit-api"),
        new("Jwt:AccessTokenLifetimeMinutes", "15"),
        new("Jwt:SigningKey:KeyId", "test-key"),
        new("Jwt:SigningKey:PrivateKeyPem", PrivateKeyPem.Value)
    ];

    /// <summary>Adds those entries to an existing settings dictionary.</summary>
    public static Dictionary<string, string?> With(Dictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var (key, value) in Settings)
        {
            settings[key] = value;
        }

        return settings;
    }
}
