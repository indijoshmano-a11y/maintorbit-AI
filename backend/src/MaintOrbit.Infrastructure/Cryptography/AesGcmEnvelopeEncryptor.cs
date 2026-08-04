using System.Security.Cryptography;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Cryptography;

/// <summary>
/// AES-256-GCM envelope encryption (SD-009).
/// </summary>
/// <remarks>
/// The Layer 3 control 09-encryption-strategy §3.3 calls "the one that matters most, and the one
/// commonly omitted" — disk encryption does not defend against a compromised application, a leaked
/// database credential, or a privileged operator; only application-layer encryption does.
/// <para>
/// <b>The nonce is generated here and never accepted from a caller.</b> §3.8 calls GCM nonce
/// uniqueness "the single most important implementation detail in this document": reuse under one
/// key allows recovery of the authentication subkey and forgery of arbitrary ciphertexts. Twelve
/// random bytes per operation from a cryptographically secure RNG makes a repeat vanishingly
/// unlikely, and no call site can get it wrong because no call site is asked.
/// </para>
/// <para>
/// <b>The Company identifier is authenticated data, not just a lookup key.</b> Binding it into the
/// tag means a ciphertext moved between Companies fails to open rather than decrypting to the same
/// secret under a different tenant — belt and braces alongside row-level security, and free.
/// </para>
/// </remarks>
internal sealed class AesGcmEnvelopeEncryptor(ICompanyDataKeyStore keys) : IEnvelopeEncryptor
{
    /// <inheritdoc />
    public SecretEnvelope Protect(CompanyId companyId, ReadOnlySpan<byte> plaintext)
    {
        var version = keys.CurrentVersion;
        var key = keys.Resolve(companyId, version)
            ?? throw new InvalidOperationException(
                "No data encryption key is available for the current version.");

        var nonce = RandomNumberGenerator.GetBytes(SecretEnvelope.NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[SecretEnvelope.TagLength];

        using var aes = new AesGcm(key, SecretEnvelope.TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(companyId));

        return SecretEnvelope.Create(ciphertext, nonce, tag, version);
    }

    /// <inheritdoc />
    public byte[]? Unprotect(CompanyId companyId, SecretEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.AlgorithmId, SecretEnvelope.AesGcm256, StringComparison.Ordinal))
        {
            // A scheme this build does not implement. Refusing beats guessing: the alternative is
            // feeding another algorithm's ciphertext to AES-GCM and reporting the resulting
            // authentication failure as tampering.
            return null;
        }

        var key = keys.Resolve(companyId, envelope.DekVersion);

        if (key is null)
        {
            return null;
        }

        var plaintext = new byte[envelope.Ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, SecretEnvelope.TagLength);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.AuthenticationTag,
                plaintext,
                AssociatedData(companyId));

            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            // What SD-009's authenticated mode is for: the ciphertext, the nonce, the tag, or the
            // Company was altered. Returning null rather than throwing keeps this an expected
            // outcome (EX-1) — it is data that has been sitting in a database, and an exception
            // would be observable in timing and in logs.
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    /// <summary>The Company identifier, bound into the tag as additional authenticated data.</summary>
    private static byte[] AssociatedData(CompanyId companyId) => companyId.Value.ToByteArray();
}
