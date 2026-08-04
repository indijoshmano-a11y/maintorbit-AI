using System.Security.Cryptography;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// Supplies a throwaway AES-256 key so composed test hosts can start.
/// </summary>
/// <remarks>
/// The encryption settings are validated at startup and have no default, so every host that
/// composes the infrastructure layer must supply one — which is the intended behaviour: a
/// deployment that cannot decrypt its C4 data should refuse to start rather than fail on the first
/// second-factor prompt.
/// <para>
/// The key is <b>generated, never committed</b>. 09-encryption-strategy §3.7 requires key material
/// to live outside source and images and be mounted at runtime, and a key checked into a test
/// fixture is a key in source — it would be found, and it would eventually be copied into a
/// deployment because it was convenient.
/// </para>
/// <para>
/// Generated once for the assembly and reused, the same as the JWT signing key: several hosts run
/// per suite and re-deriving per host would add nothing.
/// </para>
/// </remarks>
internal static class TestEncryptionKey
{
    /// <summary>A base64 AES-256 key, valid for this process only.</summary>
    public static string Base64 { get; } =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
