using MaintOrbit.Infrastructure.Maintenance;

namespace MaintOrbit.Api.FunctionalTests.Maintenance;

/// <summary>
/// Covers how a partition is recognised, named, and judged expired.
/// </summary>
/// <remarks>
/// These are the rules that decide whether audit history is destroyed, so they are tested apart
/// from the database. A mistake here does not fail loudly — it drops the wrong month, and on an
/// append-only relation there is no way back.
/// </remarks>
public sealed class AuditPartitionTests
{
    private const string JulyBound =
        "FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00')";

    [Fact]
    public void AWellFormedPartition_IsRecognised()
    {
        var partition = AuditPartition.TryRead("audit_events_2026_07", JulyBound);

        Assert.NotNull(partition);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), partition.From);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), partition.To);
    }

    [Theory]
    [InlineData("audit_events_2026_7")]
    [InlineData("audit_events_202607")]
    [InlineData("audit_events_july")]
    [InlineData("audit_events")]
    [InlineData("something_else_2026_07")]
    public void AnUnrecognisedName_IsNotParsed(string name)
    {
        // Reported to an operator rather than adopted. A relation sitting under audit_events that
        // this job does not understand is exactly the thing it must not act on.
        Assert.Null(AuditPartition.TryRead(name, JulyBound));
    }

    [Fact]
    public void BoundsThatDisagreeWithTheName_AreRejected()
    {
        // The dangerous case, and the reason name and bounds are cross-checked rather than trusted
        // separately. A partition called ..._2027_03 holding February's rows would mislead every
        // operator reading the catalogue, and a retention decision taken from the name would drop
        // the wrong month's evidence.
        var mismatched = AuditPartition.TryRead("audit_events_2026_08", JulyBound);

        Assert.Null(mismatched);
    }

    [Fact]
    public void ARangeLongerThanAMonth_IsRejected()
    {
        var quarterly = AuditPartition.TryRead(
            "audit_events_2026_07",
            "FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-10-01 00:00:00+00')");

        Assert.Null(quarterly);
    }

    [Fact]
    public void ADefaultPartition_IsNotParsedAsAMonth()
    {
        // The migration deliberately creates none, and neither does maintenance. A DEFAULT
        // partition turns a loud missing-partition outage into silent misfiling, and rows landing
        // in it then block creating the real partition for their range.
        Assert.Null(AuditPartition.TryRead("audit_events_default", "DEFAULT"));
    }

    [Fact]
    public void MonthStart_IsComputedInUtc()
    {
        // A boundary computed in local time puts the partition edge at the wrong instant for every
        // deployment outside UTC, and rows either side of midnight on the first land in the wrong
        // partition — or in none, which loses them.
        var lateInJulyLocal = new DateTimeOffset(2026, 8, 1, 1, 30, 0, TimeSpan.FromHours(13));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            AuditPartition.MonthStart(lateInJulyLocal));
    }

    [Fact]
    public void TheName_FollowsTheDocumentedScheme()
    {
        // §1.5: `<table>_<period>`, as in usage_records_2026_07.
        Assert.Equal(
            "audit_events_2026_07",
            AuditPartition.NameFor(new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)));
    }

    // ---- Retention -----------------------------------------------------------------------------

    [Fact]
    public void APartitionInsideRetention_IsNotExpired()
    {
        var july2026 = AuditPartition.TryRead("audit_events_2026_07", JulyBound)!;

        // Twelve months of retention, judged in August 2026: July is a month old.
        Assert.False(july2026.IsExpired(
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), retentionMonths: 12));
    }

    [Fact]
    public void APartitionExactlyAtTheBoundary_IsNotExpired()
    {
        // The case worth stating: retention is "at least twelve months" (AU-7), so a partition
        // whose newest possible row is exactly twelve months old is still inside it. An
        // off-by-one here destroys a month of evidence that was still required.
        var july2026 = AuditPartition.TryRead("audit_events_2026_07", JulyBound)!;

        Assert.False(july2026.IsExpired(
            new DateTimeOffset(2027, 7, 20, 0, 0, 0, TimeSpan.Zero), retentionMonths: 12));
    }

    [Fact]
    public void APartitionPastRetention_IsExpired()
    {
        var july2026 = AuditPartition.TryRead("audit_events_2026_07", JulyBound)!;

        Assert.True(july2026.IsExpired(
            new DateTimeOffset(2027, 8, 1, 0, 0, 0, TimeSpan.Zero), retentionMonths: 12));
    }

    [Fact]
    public void ExpiryIsJudgedOnTheUpperBound()
    {
        // A partition is eligible only once the *newest* row it could hold has aged out. Judging
        // the lower bound would drop a partition still holding events inside retention — the whole
        // month, silently, with no delete path to undo it.
        var july2026 = AuditPartition.TryRead("audit_events_2026_07", JulyBound)!;

        // One day before the upper bound clears twelve months.
        Assert.False(july2026.IsExpired(
            new DateTimeOffset(2027, 7, 31, 23, 59, 59, TimeSpan.Zero), retentionMonths: 12));
    }

    [Fact]
    public void ALongerRetentionKeepsPartitionsLonger()
    {
        var july2026 = AuditPartition.TryRead("audit_events_2026_07", JulyBound)!;
        var asAt = new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(july2026.IsExpired(asAt, retentionMonths: 12));
        Assert.False(july2026.IsExpired(asAt, retentionMonths: 24));
    }
}
