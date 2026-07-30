namespace MaintOrbit.Shared.Primitives;

/// <summary>
/// Generation and admission rules for correlation identifiers.
/// </summary>
/// <remarks>
/// The API specification (§12.1) states that a correlation identifier is generated at
/// ingress, that a <b>client may supply</b> one, and that it is always returned — in the
/// response header and in every error body. §4.1 permits client generation for exactly two
/// things: idempotency keys and correlation identifiers.
/// <para>
/// A client-supplied identifier is therefore untrusted input that is written into logs. It
/// is admitted only after passing <see cref="IsWellFormed"/>; anything else is replaced with
/// a generated value rather than rejected, because correlation is a diagnostic aid and
/// failing a customer's request over a malformed diagnostic header would be the wrong trade.
/// </para>
/// </remarks>
public static class CorrelationId
{
    /// <summary>
    /// Longest identifier accepted from a caller.
    /// </summary>
    /// <remarks>
    /// An inbound header is attacker-controlled and lands in every log entry for the life of
    /// the request. Without a ceiling, one request multiplies a large header across the whole
    /// log pipeline — storage, indexing, and shipping — which is cheap to send and expensive
    /// to absorb. 128 characters comfortably fits a GUID, a ULID, and the trace identifiers
    /// that upstream systems typically forward.
    /// </remarks>
    public const int MaxLength = 128;

    /// <summary>
    /// Creates a new correlation identifier.
    /// </summary>
    /// <remarks>
    /// UUIDv7 because it sorts by creation time. Correlation identifiers are read in log
    /// stores and grouped by request, so time-ordered values keep related entries adjacent
    /// rather than scattering them the way UUIDv4 does. Formatted without hyphens to keep
    /// log lines and URLs compact.
    /// </remarks>
    public static string New() => Guid.CreateVersion7().ToString("n");

    /// <summary>
    /// Returns <paramref name="candidate"/> when a caller supplied a usable one, otherwise a
    /// newly generated identifier.
    /// </summary>
    /// <remarks>
    /// This is the whole "generate when absent, reuse when present" rule in one place, so
    /// that ingress cannot accidentally implement half of it.
    /// </remarks>
    public static string Resolve(string? candidate) =>
        IsWellFormed(candidate) ? candidate : New();

    /// <summary>
    /// Whether a caller-supplied value may be admitted as a correlation identifier.
    /// </summary>
    /// <remarks>
    /// Allowlist, not denylist (VL-3): ASCII letters, digits, hyphen, and underscore. The
    /// characters this excludes are the point — a carriage return or newline in a value that
    /// is written to a line-oriented log lets a caller forge log entries, and control
    /// characters corrupt the parsers that NFR-OBS-001 requires the logs to be readable by.
    /// </remarks>
    public static bool IsWellFormed([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var permitted = char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
            if (!permitted)
            {
                return false;
            }
        }

        return true;
    }
}
