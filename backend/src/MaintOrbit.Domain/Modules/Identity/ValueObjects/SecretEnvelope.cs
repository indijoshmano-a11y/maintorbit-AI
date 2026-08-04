using System.Diagnostics;

namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// An AES-256-GCM envelope: ciphertext plus everything needed to open it — C4 data.
/// </summary>
/// <remarks>
/// The shape 06-database §4.3 gives for <c>provider_connections</c>, which §4.2 says MFA secrets
/// follow: "TOTP secret stored encrypted under the Company DEK using the same envelope scheme as
/// Provider Credentials". Every part is load-bearing, and §4.3 says why:
/// <list type="bullet">
/// <item><see cref="Nonce"/> is unique per encryption operation. 09-encryption-strategy §3.8 calls
/// GCM nonce uniqueness "the single most important implementation detail in this document" —
/// reuse under one key allows forgery of arbitrary ciphertexts.</item>
/// <item><see cref="AuthenticationTag"/> is what makes SD-009 authenticated encryption. Without
/// it, tampering is decrypted silently rather than detected.</item>
/// <item><see cref="DekVersion"/> implements SD-012. Old ciphertext stays readable under the
/// version that produced it, which is what makes key rotation incremental instead of a
/// synchronized rewrite of everything.</item>
/// <item><see cref="AlgorithmId"/> records the scheme, so a future algorithm change is additive
/// rather than a guess about what an old row was encrypted with.</item>
/// </list>
/// <para>
/// <b>It refuses to print itself.</b> The ciphertext alone is not a secret, but a type that prints
/// its contents is one an aggregate prints in any log line that formats it, and this sits on a C4
/// table.
/// </para>
/// <para>
/// <b>A class, not a record</b>, unlike the other value objects here. Record equality over
/// <see cref="byte"/> arrays compares references, so two envelopes holding identical bytes would
/// report themselves unequal — a value object whose equality is a lie is worse than one with
/// none. Nothing compares envelopes, and this makes that explicit rather than incidental.
/// </para>
/// </remarks>
[DebuggerDisplay("SecretEnvelope [REDACTED]")]
public sealed class SecretEnvelope
{
    /// <summary>The AES-256-GCM scheme identifier recorded on every row.</summary>
    /// <remarks>
    /// SD-009 fixes the algorithm; this names it in the data so a row can be read without
    /// assuming what the code was doing when it was written.
    /// </remarks>
    public const string AesGcm256 = "aes-256-gcm";

    /// <summary>GCM's nonce length in bytes. 96 bits is the size the mode is defined for.</summary>
    public const int NonceLength = 12;

    /// <summary>GCM's authentication tag length in bytes.</summary>
    public const int TagLength = 16;

    private SecretEnvelope(
        byte[] ciphertext, byte[] nonce, byte[] authenticationTag, int dekVersion, string algorithmId)
    {
        Ciphertext = ciphertext;
        Nonce = nonce;
        AuthenticationTag = authenticationTag;
        DekVersion = dekVersion;
        AlgorithmId = algorithmId;
    }

    /// <summary>The encrypted secret.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>The nonce this ciphertext was produced under. Never reused with the same key.</summary>
    public byte[] Nonce { get; }

    /// <summary>The GCM authentication tag.</summary>
    public byte[] AuthenticationTag { get; }

    /// <summary>Which generation of the Company's data encryption key produced it (SD-012).</summary>
    public int DekVersion { get; }

    /// <summary>Which scheme produced it.</summary>
    public string AlgorithmId { get; }

    /// <summary>
    /// Creates an envelope from parts already produced by the encryptor.
    /// </summary>
    /// <remarks>
    /// Lengths are checked rather than trusted. A nonce or tag of the wrong size cannot have come
    /// from AES-GCM, and the failure it would otherwise cause is at decryption time — on a row
    /// that is already written, for an Employee who is already locked out of their second factor.
    /// </remarks>
    /// <exception cref="ArgumentException">A part is empty or the wrong length, or the version is
    /// not positive.</exception>
    public static SecretEnvelope Create(
        byte[] ciphertext,
        byte[] nonce,
        byte[] authenticationTag,
        int dekVersion,
        string algorithmId = AesGcm256)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(authenticationTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmId);
        ArgumentOutOfRangeException.ThrowIfLessThan(dekVersion, 1);

        if (ciphertext.Length == 0)
        {
            throw new ArgumentException("An envelope must carry ciphertext.", nameof(ciphertext));
        }

        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException(
                $"A GCM nonce is {NonceLength} bytes.", nameof(nonce));
        }

        if (authenticationTag.Length != TagLength)
        {
            throw new ArgumentException(
                $"A GCM authentication tag is {TagLength} bytes.", nameof(authenticationTag));
        }

        return new SecretEnvelope(ciphertext, nonce, authenticationTag, dekVersion, algorithmId);
    }

    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";
}
