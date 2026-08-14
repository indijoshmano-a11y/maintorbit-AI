using System.Globalization;
using System.Text;

namespace MaintOrbit.Application.Abstractions.Auditing;

/// <summary>
/// The position a page of Audit Events continues from.
/// </summary>
/// <remarks>
/// <b>Composition is <c>(occurredAtUtc, id)</c>, matching the index order exactly</b> — §5.4 states
/// it, and matching matters: a keyset predicate whose columns differ from the index cannot be
/// served by a seek and degrades into the scan that offset pagination was rejected for.
/// <para>
/// <b>It carries no Company, and that is the point.</b> A cursor that named a tenant would be a
/// tenant selector supplied by the client — TC-1's forbidden shape, wearing a different hat.
/// Continuing a page re-runs the same tenant-scoped query, so a cursor lifted from another
/// Company's response yields that Company's rows only if row-level security has already failed;
/// the cursor cannot cause it to.
/// </para>
/// <para>
/// <b>Opaque, not signed.</b> §5.4 fixes only that the encoding "is not a contract"; nothing in
/// the architecture documents a cursor-protection mechanism, so none is invented here. It does not
/// need one: it holds a timestamp and an identifier the caller has already been shown, and it
/// confers no authority — every predicate it participates in is still filtered by the database
/// against the caller's own tenant.
/// </para>
/// </remarks>
public sealed record AuditCursor(DateTimeOffset OccurredAtUtc, Guid Id, string Fingerprint)
{
    /// <summary>How long a cursor remains usable (§5.4 — "cursors expire after a bounded period").</summary>
    /// <remarks>
    /// Bounded so a cursor cannot be replayed against a materially different dataset weeks later,
    /// by which time retention may have removed the partition it points into.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Field separator inside the encoded cursor.
    /// </summary>
    /// <remarks>
    /// ASCII unit separator. Chosen because it cannot occur in any of the four fields — ticks and
    /// a hyphen-free GUID are alphanumeric — so splitting is unambiguous without escaping.
    /// </remarks>
    private const char Separator = '\u001f';

    /// <summary>Encodes the cursor for transport.</summary>
    public string Encode(DateTimeOffset issuedAtUtc) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
            Separator,
            OccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Id.ToString("n"),
            Fingerprint,
            issuedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture))));

    /// <summary>
    /// Decodes a cursor, refusing anything malformed, stale, or belonging to a different query.
    /// </summary>
    /// <remarks>
    /// Every failure returns <see langword="false"/> rather than throwing: the input comes from a
    /// query string, so a malformed value is an ordinary client mistake that must produce a
    /// validation error, not a 500.
    /// </remarks>
    public static bool TryDecode(
        string? encoded, string expectedFingerprint, DateTimeOffset nowUtc, out AuditCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        // Sized from the input rather than a guess, and bounded: a caller cannot make the server
        // allocate by sending a long "cursor".
        if (encoded.Length > 512)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[512];

        if (!Convert.TryFromBase64String(encoded, buffer, out var written))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split(Separator);

        if (parts.Length != 4
            || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
            || !Guid.TryParseExact(parts[1], "N", out var id)
            || !long.TryParse(parts[3], CultureInfo.InvariantCulture, out var issuedTicks))
        {
            return false;
        }

        // §5.4: a filter change invalidates the cursor, and it must be an error rather than
        // silent misbehaviour — continuing with the old keyset against a new predicate would skip
        // rows without saying so.
        if (!string.Equals(parts[2], expectedFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var issued = new DateTimeOffset(issuedTicks, TimeSpan.Zero);

        if (issued > nowUtc.AddMinutes(1) || nowUtc - issued > Lifetime)
        {
            return false;
        }

        cursor = new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id, parts[2]);

        return true;
    }
}
