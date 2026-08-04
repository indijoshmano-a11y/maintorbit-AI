using System.Security.Cryptography;
using System.Text;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Generates recovery codes and hashes presented ones with SHA-256.
/// </summary>
/// <remarks>
/// 09-encryption-strategy §3.8 requires a cryptographically secure RNG for recovery codes;
/// <see cref="RandomNumberGenerator"/> is that. A guessable recovery code is a bypass of the
/// second factor with nothing else required.
/// <para>
/// SHA-256 rather than Argon2id, per §3's decision tree — the secret is high-entropy and generated
/// by us, so brute force is infeasible regardless. Unsalted, which is what allows lookup by
/// digest; a per-row salt would make finding a presented code a table scan.
/// </para>
/// </remarks>
internal sealed class RecoveryCodeFactory(IOptions<MfaOptions> options) : IRecoveryCodeFactory
{
    /// <summary>
    /// Crockford's base32 alphabet, without I, L, O, and U.
    /// </summary>
    /// <remarks>
    /// These codes are read off a screen and typed by a person, often from a printout. Excluding
    /// the characters that look like one another removes the transcription errors that would
    /// otherwise burn a single-use code on a misread.
    /// </remarks>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <inheritdoc />
    public IReadOnlyList<IssuedRecoveryCode> IssueSet()
    {
        var count = options.Value.RecoveryCodeCount;
        var codes = new List<IssuedRecoveryCode>(count);

        for (var i = 0; i < count; i++)
        {
            var code = Generate(options.Value.RecoveryCodeBytes);

            codes.Add(new IssuedRecoveryCode(code, Hash(code)));
        }

        return codes;
    }

    /// <inheritdoc />
    public RecoveryCodeHash Hash(string presentedCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedCode);

        // Normalized before hashing, so an Employee who types their code in lower case or leaves
        // the separator out still matches the row. The alphabet has no lower-case members, so this
        // widens what is accepted without widening what exists.
        var normalized = presentedCode
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return RecoveryCodeHash.Create(Convert.ToHexStringLower(digest));
    }

    private static string Generate(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);

        try
        {
            var builder = new StringBuilder(byteCount * 2);

            foreach (var b in bytes)
            {
                // Two characters per byte from a 32-character alphabet uses 10 of the 8 bits
                // available, so entropy is bounded by the bytes rather than by the encoding.
                builder.Append(Alphabet[b >> 3]);
                builder.Append(Alphabet[((b & 0x07) << 2) | (b >> 6)]);
            }

            // Grouped for transcription. Stripped again before hashing, so the separator is
            // presentation only and never part of the secret.
            var raw = builder.ToString();

            return $"{raw[..(raw.Length / 2)]}-{raw[(raw.Length / 2)..]}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
