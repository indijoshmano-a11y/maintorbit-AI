using System.Globalization;
using System.Text;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Encodes and decodes the PHC string format.
/// </summary>
/// <remarks>
/// <c>$argon2id$v=19$m=65536,t=3,p=4$&lt;salt&gt;$&lt;hash&gt;</c> — the format Argon2's reference
/// implementation and every mainstream library produce.
/// <para>
/// The format is used rather than four separate columns because it makes a hash
/// <b>self-describing</b>. §4.2 requires that an annual parameter change not invalidate existing
/// hashes; a stored string that carries its own costs and salt can always be verified, even by a
/// build whose configured parameters have moved on, and even if the row's
/// <c>hash_parameters</c> column were lost.
/// </para>
/// <para>
/// Base64 here is unpadded, per the PHC specification. Padding characters are omitted rather
/// than stripped on read, so a value produced here round-trips through any conforming
/// implementation.
/// </para>
/// </remarks>
internal static class PhcString
{
    /// <summary>Identifier for the Argon2id variant.</summary>
    public const string Argon2idIdentifier = "argon2id";

    /// <summary>
    /// The Argon2 algorithm revision, 0x13.
    /// </summary>
    /// <remarks>
    /// Not the parameter generation. This number belongs to the specification and changes only
    /// if Argon2 itself is revised.
    /// </remarks>
    public const int Argon2Version = 19;

    /// <summary>
    /// Builds a PHC string.
    /// </summary>
    public static string Encode(
        int memoryKibibytes,
        int iterations,
        int parallelism,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> hash)
    {
        var builder = new StringBuilder(128);

        builder.Append('$').Append(Argon2idIdentifier)
            .Append("$v=").Append(Argon2Version.ToString(CultureInfo.InvariantCulture))
            .Append("$m=").Append(memoryKibibytes.ToString(CultureInfo.InvariantCulture))
            .Append(",t=").Append(iterations.ToString(CultureInfo.InvariantCulture))
            .Append(",p=").Append(parallelism.ToString(CultureInfo.InvariantCulture))
            .Append('$').Append(ToUnpaddedBase64(salt))
            .Append('$').Append(ToUnpaddedBase64(hash));

        return builder.ToString();
    }

    /// <summary>
    /// Parses a PHC string.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> rather than throwing for anything malformed. A stored hash
    /// that will not parse is a data fault the caller reports as an unusable credential, and an
    /// exception thrown from here would carry the malformed value — which is C4 material — into
    /// whatever logged it.
    /// </remarks>
    public static bool TryDecode(string? encoded, out PhcHash result)
    {
        result = default;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        // Leading empty segment from the initial '$'.
        var parts = encoded.Split('$');

        if (parts.Length != 6
            || parts[0].Length != 0
            || !string.Equals(parts[1], Argon2idIdentifier, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadTagged(parts[2], "v", out var version)
            || !TryReadCosts(parts[3], out var memory, out var iterations, out var parallelism)
            || !TryFromUnpaddedBase64(parts[4], out var salt)
            || !TryFromUnpaddedBase64(parts[5], out var hash))
        {
            return false;
        }

        if (salt.Length == 0 || hash.Length == 0)
        {
            return false;
        }

        result = new PhcHash(version, memory, iterations, parallelism, salt, hash);
        return true;
    }

    private static bool TryReadCosts(
        string segment, out int memory, out int iterations, out int parallelism)
    {
        memory = iterations = parallelism = 0;

        var fields = segment.Split(',');

        return fields.Length == 3
               && TryReadTagged(fields[0], "m", out memory)
               && TryReadTagged(fields[1], "t", out iterations)
               && TryReadTagged(fields[2], "p", out parallelism)
               && memory > 0 && iterations > 0 && parallelism > 0;
    }

    /// <summary>Reads a <c>name=value</c> field where the value is a positive integer.</summary>
    private static bool TryReadTagged(string field, string name, out int value)
    {
        value = 0;

        return field.Length > name.Length + 1
               && field.AsSpan(0, name.Length).SequenceEqual(name)
               && field[name.Length] == '='
               // NumberStyles.None rejects a sign, whitespace, and grouping, so "+3" and " 3"
               // do not become 3.
               && int.TryParse(
                   field.AsSpan(name.Length + 1),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static string ToUnpaddedBase64(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryFromUnpaddedBase64(string value, out byte[] decoded)
    {
        decoded = [];

        if (value.Length == 0 || value.Contains('=', StringComparison.Ordinal))
        {
            // Padding present means the producer did not follow the PHC format. Accepting it
            // would make this the lenient reader that hides a non-conforming writer.
            return false;
        }

        var padding = (4 - (value.Length % 4)) % 4;

        if (padding == 3)
        {
            // A remainder of one character cannot come from any byte sequence.
            return false;
        }

        var buffer = new byte[((value.Length + padding) / 4) * 3];

        if (!Convert.TryFromBase64String(value + new string('=', padding), buffer, out var written))
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}

/// <summary>
/// A decoded PHC hash.
/// </summary>
/// <remarks>
/// Carries the salt and derived hash as arrays because they are passed to the key derivation
/// function, which takes arrays. Both are C4 material; nothing here formats them.
/// </remarks>
internal readonly record struct PhcHash(
    int Version,
    int MemoryKibibytes,
    int Iterations,
    int Parallelism,
    byte[] Salt,
    byte[] Hash);
