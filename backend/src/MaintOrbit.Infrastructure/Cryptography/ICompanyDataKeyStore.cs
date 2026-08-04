using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.Cryptography;

/// <summary>
/// Supplies a Company's data encryption key, unwrapped and ready to use.
/// </summary>
/// <remarks>
/// <b>This is the seam where the documented key hierarchy plugs in, and it is not filled yet.</b>
/// 10-key-management §3.1 puts the key-encryption key with a custodian outside the database and
/// one DEK per Company inside it, wrapped; 06-database §4.3 gives that table as
/// <c>providers.company_data_keys</c>. The <c>providers</c> module does not exist, and identity
/// must not create or write another module's table (ADR-0002, CLAUDE.md §7) — so the store is
/// declared here and satisfied by a deployment key until that module lands.
/// <para>
/// Internal to infrastructure on purpose. Key material must not be reachable from the application
/// layer at all; what crosses that boundary is <c>IEnvelopeEncryptor</c>, which deals in sealed
/// envelopes and never in keys.
/// </para>
/// </remarks>
internal interface ICompanyDataKeyStore
{
    /// <summary>The version new ciphertext should be written under (SD-012).</summary>
    int CurrentVersion { get; }

    /// <summary>
    /// The key for a Company at a given version, or <see langword="null"/> if that version is
    /// unknown to this deployment.
    /// </summary>
    /// <remarks>
    /// Versioned rather than current-only, because 09-encryption-strategy §3.6 makes rotation
    /// incremental: old ciphertext stays readable under the version that produced it while new
    /// writes use the current one. A store that could only answer "the current key" would turn
    /// every rotation into a synchronized rewrite of everything.
    /// </remarks>
    byte[]? Resolve(CompanyId companyId, int version);
}
