using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// TOTP as RFC 6238 defines it, over RFC 4226's HOTP.
/// </summary>
/// <remarks>
/// <b>Implemented rather than taken from a package, and the documentation says to.</b>
/// backend-technologies §8 lists <c>Otp.NET</c> at 🟡 with "evaluate maintenance health", and §12
/// names the mitigation itself: "TOTP is a documented standard; the algorithm is straightforward
/// to implement or substitute". This is that substitution — the same call the architecture tests
/// took over reflection instead of a small library.
/// <para>
/// <b>This is not bespoke cryptography.</b> 09-encryption-strategy §1's rule is that "the
/// platform's cryptographic risk should be in <i>how</i> primitives are composed, never in the
/// primitives themselves". The primitive here is <see cref="HMACSHA1"/> from the framework; what
/// this file contributes is RFC 4226 §5.3's dynamic truncation and RFC 6238 §4's counter, both of
/// which are published, fixed, and testable against the RFCs' own vectors.
/// </para>
/// <para>
/// <b>HMAC-SHA1, and that is correct here.</b> SHA-1 is forbidden by §3.1 "for any security
/// purpose", which means collision resistance — the property SHA-1 has lost. HMAC does not rely on
/// it, and every authenticator app implements RFC 6238's default. Choosing SHA-256 would produce
/// codes no Employee's app can generate.
/// </para>
/// </remarks>
internal sealed class Rfc6238TotpService(IOptions<MfaOptions> options) : ITotpService
{
    /// <summary>Seconds per step — RFC 6238 §5.2's recommended default, and every app's.</summary>
    private const int StepSeconds = 30;

    /// <summary>Digits in a code — RFC 4226 §5.3's minimum and the universal choice.</summary>
    private const int Digits = 6;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <inheritdoc />
    public byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(options.Value.SecretBytes);

    /// <inheritdoc />
    /// <remarks>
    /// Base32 without padding, which is what the Key Uri Format specifies and what authenticator
    /// apps accept for manual entry.
    /// </remarks>
    public string Encode(ReadOnlySpan<byte> secret)
    {
        var builder = new StringBuilder((secret.Length * 8 / 5) + 1);
        var buffer = 0;
        var bitsHeld = 0;

        foreach (var b in secret)
        {
            buffer = (buffer << 8) | b;
            bitsHeld += 8;

            while (bitsHeld >= 5)
            {
                bitsHeld -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsHeld) & 31]);
            }
        }

        if (bitsHeld > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsHeld)) & 31]);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public long TimeStepAt(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds() / StepSeconds;

    /// <inheritdoc />
    public bool IsValid(ReadOnlySpan<byte> secret, string presentedCode, DateTimeOffset asAt)
    {
        if (string.IsNullOrEmpty(presentedCode) || presentedCode.Length != Digits)
        {
            return false;
        }

        Span<char> expected = stackalloc char[Digits];
        Compute(secret, TimeStepAt(asAt), expected);

        // Fixed-time comparison. A byte-by-byte comparison that returns early leaks how many
        // leading digits were right, which turns 10^6 guesses into about 60.
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(expected),
            MemoryMarshal.AsBytes(presentedCode.AsSpan()));
    }

    /// <summary>
    /// RFC 4226 §5.3 — HMAC the counter, then dynamically truncate.
    /// </summary>
    /// <remarks>
    /// The truncation is the part worth reading carefully. The low four bits of the last byte
    /// select an offset; four bytes are read from there; the top bit is masked off because it is
    /// the sign bit and a negative modulus would be a different code on a different platform.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "HMAC-SHA1 is RFC 6238's default and what every authenticator app implements; a " +
            "different digest would produce codes no Employee's app can generate. The analyser " +
            "flags SHA-1's loss of collision resistance, which HMAC does not rely on — and " +
            "09-encryption-strategy §3.1 forbids SHA-1 'for any security purpose' in that sense, " +
            "not as a keyed MAC inside a published standard. Interoperability is the security " +
            "property here: an unusable second factor is one nobody enables.")]
    private static void Compute(ReadOnlySpan<byte> secret, long counter, Span<char> destination)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, message, mac);

        var offset = mac[^1] & 0x0F;

        var binary =
            ((mac[offset] & 0x7F) << 24) |
            (mac[offset + 1] << 16) |
            (mac[offset + 2] << 8) |
            mac[offset + 3];

        var code = binary % 1_000_000;

        for (var position = Digits - 1; position >= 0; position--)
        {
            destination[position] = (char)('0' + (code % 10));
            code /= 10;
        }
    }
}
