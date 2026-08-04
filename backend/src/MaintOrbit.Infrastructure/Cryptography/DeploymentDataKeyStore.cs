using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Cryptography;

/// <summary>
/// One deployment-wide key, standing in for per-Company data encryption keys.
/// </summary>
/// <remarks>
/// <b>This is a documented shortfall, not the target design, and it is named so it cannot be
/// mistaken for one.</b> 10-key-management §3.1 requires one DEK per Company so that "a key that
/// protects two unrelated things doubles the consequence of its compromise" — here one key
/// protects every Company's MFA secrets, so compromising it compromises all of them at once.
/// <para>
/// <b>Two things block the real hierarchy, and neither is this milestone's to settle.</b> The
/// wrapped DEKs live in <c>providers.company_data_keys</c> (06-database §4.3), and the
/// <c>providers</c> module does not exist — identity may not create or write another module's
/// table (ADR-0002, CLAUDE.md §7). And <b>D-6</b>, the key custodian with its tested backup and
/// escrow procedure, is an open decision in CLAUDE.md §5, which rule 10 says to stop at rather
/// than choose.
/// </para>
/// <para>
/// <b>What is already right, so the change is a re-encrypt and not a redesign.</b> The cipher is
/// AES-256-GCM (SD-009); every envelope records its key version (SD-012) and its algorithm; and
/// <see cref="ICompanyDataKeyStore"/> takes a Company on every call. When the providers module
/// lands, this class is replaced behind that interface and existing rows are re-encrypted under
/// their Company's DEK at a new version — with the old version still readable throughout, which is
/// the property §3.6 says the version column exists to give.
/// </para>
/// </remarks>
internal sealed class DeploymentDataKeyStore : ICompanyDataKeyStore
{
    private readonly byte[] _key;

    public DeploymentDataKeyStore(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Decoded once. The validator has already established that this is 32 bytes of base64, so
        // a failure here would be a defect rather than a configuration error.
        _key = Convert.FromBase64String(options.Value.DataKey);
        CurrentVersion = options.Value.DataKeyVersion;
    }

    /// <inheritdoc />
    public int CurrentVersion { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The Company is accepted and deliberately ignored — the parameter is the seam, and removing
    /// it would mean adding it back to every call site later. A version this deployment does not
    /// hold returns null, which the encryptor turns into a failed decryption rather than a wrong
    /// one.
    /// </remarks>
    public byte[]? Resolve(CompanyId companyId, int version) =>
        version == CurrentVersion ? _key : null;
}
