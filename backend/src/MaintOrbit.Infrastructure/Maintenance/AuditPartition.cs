using System.Globalization;
using System.Text.RegularExpressions;

namespace MaintOrbit.Infrastructure.Maintenance;

/// <summary>
/// One partition of <c>auditing.audit_events</c>, as the catalogue actually reports it.
/// </summary>
/// <remarks>
/// <b>Read from the database, never assumed from the migration.</b> The migration created a fixed
/// window on the day it ran; everything after that is this job's doing, and a job that trusted its
/// own past output would not notice a partition dropped by hand, a range created wrongly, or a
/// deployment restored from a backup taken before the window moved.
/// </remarks>
internal sealed record AuditPartition(string Name, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>The naming scheme from <c>06-database</c> §1.5 — <c>&lt;table&gt;_&lt;period&gt;</c>.</summary>
    public const string Prefix = "audit_events_";

    private static readonly Regex NamePattern = new(
        @"^audit_events_(?<year>\d{4})_(?<month>\d{2})$",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Parses the bound expression PostgreSQL reports for a range partition.
    /// </summary>
    /// <remarks>
    /// <c>pg_get_expr(relpartbound, oid)</c> renders as
    /// <c>FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00')</c>. Parsing
    /// that string is unattractive, and it is still the right source: the alternative is reading
    /// <c>relpartbound</c> as a raw node tree, which is a private catalogue representation with no
    /// stability guarantee across major versions.
    /// </remarks>
    private static readonly Regex BoundPattern = new(
        @"FOR VALUES FROM \('(?<from>[^']+)'\) TO \('(?<to>[^']+)'\)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>The partition name covering a given instant.</summary>
    public static string NameFor(DateTimeOffset month) =>
        $"{Prefix}{month.UtcDateTime:yyyy_MM}";

    /// <summary>The first instant of the UTC month containing <paramref name="instant"/>.</summary>
    /// <remarks>
    /// UTC throughout (§1.7). A boundary computed in local time would place the partition edge at
    /// the wrong instant for every deployment outside UTC, and the rows either side of midnight on
    /// the first of the month would land in the wrong one — or in none.
    /// </remarks>
    public static DateTimeOffset MonthStart(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Reads a partition from its catalogue name and bound expression.
    /// </summary>
    /// <returns>
    /// The partition, or <see langword="null"/> if the name or bounds do not match the scheme.
    /// A null result is reported to the operator, never repaired.
    /// </returns>
    public static AuditPartition? TryRead(string name, string boundExpression)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(boundExpression);

        var named = NamePattern.Match(name);
        var bounds = BoundPattern.Match(boundExpression);

        if (!named.Success || !bounds.Success)
        {
            return null;
        }

        if (!TryParseInstant(bounds.Groups["from"].Value, out var from)
            || !TryParseInstant(bounds.Groups["to"].Value, out var to))
        {
            return null;
        }

        var year = int.Parse(named.Groups["year"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(named.Groups["month"].Value, CultureInfo.InvariantCulture);

        var expectedFrom = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var expectedTo = expectedFrom.AddMonths(1);

        // The name and the bounds must agree. A partition called audit_events_2027_03 whose range
        // covers February is worse than one that is simply missing: every operator reading the
        // catalogue would draw the wrong conclusion about where March's evidence lives, and a
        // retention calculation based on the name would drop the wrong month.
        if (from != expectedFrom || to != expectedTo)
        {
            return null;
        }

        return new AuditPartition(name, from, to);
    }

    private static bool TryParseInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out instant);

    /// <summary>
    /// Whether every event this partition can hold is older than the retention period.
    /// </summary>
    /// <remarks>
    /// Compares the <b>upper</b> bound, which is what makes this safe. A partition is eligible only
    /// once the newest row it could possibly contain has aged out — testing the lower bound would
    /// drop a partition still holding events inside retention, and on an append-only relation there
    /// is no way to get them back.
    /// </remarks>
    public bool IsExpired(DateTimeOffset asAtUtc, int retentionMonths) =>
        To <= MonthStart(asAtUtc).AddMonths(-retentionMonths);
}
