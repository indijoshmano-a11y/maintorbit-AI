using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Cryptography;

/// <summary>Application-layer encryption settings.</summary>
/// <remarks>
/// The key arrives from configuration and is mounted at runtime, which is how
/// 09-encryption-strategy §3.7 says key material reaches the process: "Never in source, never in
/// images; mounted at runtime". There is no default and no fallback — a deployment that cannot
/// decrypt its C4 data must refuse to start rather than discover it on the first MFA verification.
/// </remarks>
public sealed class EncryptionOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Encryption";

    /// <summary>Bytes in an AES-256 key.</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// The base64 AES-256 key protecting C4 material.
    /// </summary>
    /// <remarks>
    /// <b>Never a literal in any committed file.</b> Configuration files carry the empty
    /// placeholder; a real value comes from the environment or a mounted secret. A key checked
    /// into source is a key that will be found and eventually copied into a deployment because it
    /// was convenient.
    /// </remarks>
    public string DataKey { get; init; } = string.Empty;

    /// <summary>
    /// The version stamped on new ciphertext (SD-012).
    /// </summary>
    /// <remarks>
    /// Incremented when the key changes, so rows written under the old one remain identifiable.
    /// Rotation itself needs the <c>company_data_keys</c> store, which is not built.
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int DataKeyVersion { get; init; } = 1;
}

/// <summary>Validates the key material at startup.</summary>
/// <remarks>
/// Checked here rather than at first use, so a misconfigured deployment fails to start instead of
/// failing on an Employee's second-factor prompt. That is the same fail-fast posture the JWT
/// signing key already takes.
/// </remarks>
internal sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DataKey))
        {
            return ValidateOptionsResult.Fail(
                $"{EncryptionOptions.SectionName}:{nameof(EncryptionOptions.DataKey)} is required. " +
                "Supply a base64 AES-256 key from the environment or a mounted secret.");
        }

        // Sized to hold whatever the input decodes to, not to the key length. TryFromBase64String
        // also returns false when the destination is merely too small, so a buffer sized to the
        // expected key reports a 64-byte key as "not base64" — which sends an operator looking for
        // a corrupted value instead of the wrong one. Both still fail closed; only one is
        // actionable. Base64 never expands beyond three bytes per four characters.
        var decoded = new byte[((options.DataKey.Length / 4) + 1) * 3];

        try
        {
            if (!Convert.TryFromBase64String(options.DataKey, decoded, out var written))
            {
                return ValidateOptionsResult.Fail(
                    $"{EncryptionOptions.SectionName}:{nameof(EncryptionOptions.DataKey)} is not base64.");
            }

            if (written != EncryptionOptions.KeyLength)
            {
                // SD-009 fixes AES-256. A shorter key would silently select a weaker cipher, or
                // throw at the first encryption — long after the deployment was declared healthy.
                return ValidateOptionsResult.Fail(
                    $"{EncryptionOptions.SectionName}:{nameof(EncryptionOptions.DataKey)} must decode " +
                    $"to {EncryptionOptions.KeyLength} bytes for AES-256; it decodes to {written}.");
            }

            return ValidateOptionsResult.Success;
        }
        finally
        {
            // This buffer held the deployment's data key. Validation runs once at startup, so the
            // array would otherwise sit on the heap for the process lifetime with nothing
            // referencing it — present in any dump taken afterwards, for no benefit at all.
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
