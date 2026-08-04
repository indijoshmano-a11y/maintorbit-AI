using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Seals and opens C4 material under a Company's data encryption key.
/// </summary>
/// <remarks>
/// ADR-0008's envelope scheme, declared as a port so the application layer can protect a secret
/// without knowing the algorithm, the key hierarchy, or where the key-encryption key lives.
/// 09-encryption-strategy §3.1 fixes AES-256-GCM (SD-009) and §3.6 requires every ciphertext to
/// record the DEK version that produced it (SD-012); both live behind this seam.
/// <para>
/// <b>Unlike a Provider Credential, this material is meant to be opened.</b> SD-003 says no
/// plaintext retrieval path exists for Provider Credentials, and none does — but a TOTP secret is
/// useless unless the server can recompute codes from it, and 10-key-management §7 grants the
/// application "unwrap and use" on DEKs for exactly that reason. The two are the same scheme with
/// different retrieval rules, and conflating them would either break MFA or weaken SD-003.
/// </para>
/// </remarks>
public interface IEnvelopeEncryptor
{
    /// <summary>Seals plaintext for a Company.</summary>
    /// <remarks>
    /// A fresh nonce per call. 09-encryption-strategy §3.8 calls GCM nonce uniqueness "the single
    /// most important implementation detail in this document"; the caller cannot supply one, so
    /// the caller cannot get it wrong.
    /// </remarks>
    SecretEnvelope Protect(CompanyId companyId, ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Opens an envelope for a Company.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> rather than throwing when the envelope does not
    /// authenticate. Tampering and an unknown DEK version are expected outcomes on data that has
    /// been sitting in a database, and an exception here would be observable in timing and in
    /// logs (EX-1). The caller treats null as a failed verification.
    /// </remarks>
    byte[]? Unprotect(CompanyId companyId, SecretEnvelope envelope);
}
