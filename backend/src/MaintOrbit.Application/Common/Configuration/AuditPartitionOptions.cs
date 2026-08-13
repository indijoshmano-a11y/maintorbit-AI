using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Common.Configuration;

/// <summary>
/// Settings for <c>auditing.audit_events</c> partition maintenance.
/// </summary>
/// <remarks>
/// <b>Deployment-wide, not per-Company.</b> Partitions are a property of the relation, and the
/// relation is shared by every tenant — one Company cannot have a different partition layout from
/// another. FR-AUD-007's "configurable retention" is a platform setting here; if it ever becomes a
/// per-Company one, it changes what a partition may contain and needs a different mechanism than
/// dropping whole partitions.
/// </remarks>
public sealed class AuditPartitionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "AuditPartitions";

    /// <summary>
    /// The documented retention floor, in months.
    /// </summary>
    /// <remarks>
    /// <c>12-audit-and-compliance</c> §3.2 (AU-7) and §11, and <c>06-database</c> §7.2, all state
    /// audit retention as <b>≥ 12 months</b>. The exact default above that floor is decision D-5,
    /// still open with Product and Legal — so this is enforced as a minimum rather than assumed as
    /// the answer. A deployment may keep audit events longer; it may not keep them for less.
    /// </remarks>
    public const int MinimumRetentionMonths = 12;

    /// <summary>How often a maintenance cycle runs.</summary>
    /// <remarks>
    /// Daily by default. ADR-0014 lists retention enforcement as a scheduled daily job, and
    /// partition creation has a horizon measured in months, so nothing is gained by running it
    /// more often — while every run takes an exclusive advisory lock.
    /// </remarks>
    [Range(1, 1_440)]
    public int IntervalMinutes { get; init; } = 1_440;

    /// <summary>
    /// How many months of partitions must exist ahead of the current month.
    /// </summary>
    /// <remarks>
    /// The safety margin between "the job stopped working" and "audit events start being lost".
    /// Three months is the floor because it survives a job that has been silently broken for a
    /// quarter — long enough for somebody to notice a Worker that is not running. Twelve is the
    /// default, matching what the 12.2 migration created.
    /// </remarks>
    [Range(3, 36)]
    public int FutureMonths { get; init; } = 12;

    /// <summary>How long audit events are kept before their partition becomes eligible to drop.</summary>
    [Range(MinimumRetentionMonths, 120)]
    public int RetentionMonths { get; init; } = MinimumRetentionMonths;

    /// <summary>
    /// Whether expired partitions are actually dropped.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and that is a blocker rather than a preference.</b> A partition may hold
    /// events under a legal hold, and legal holds are not built — <c>legal_holds</c> is specified
    /// in <c>06-database</c> §4.10 and unimplemented (register item I-11). With no way to ask
    /// whether a hold applies, an automated drop could destroy evidence that a hold exists
    /// precisely to preserve.
    /// <para>
    /// §7.2 already frames the risk from the other direction: "reducing a retention period is a
    /// compliance-relevant act, potentially an attempt to destroy evidence". A job that deletes
    /// audit history unattended, in a product sold on the completeness of its audit trail, is not
    /// a default worth having.
    /// </para>
    /// <para>
    /// Retention is still evaluated every cycle and eligible partitions are reported, so an
    /// operator can see exactly what would be removed. Turning this on is a deliberate act by
    /// someone who has confirmed no hold applies.
    /// </para>
    /// </remarks>
    public bool DropExpiredPartitions { get; init; }

    /// <summary>
    /// How long to wait for the maintenance lock before giving up for this cycle.
    /// </summary>
    /// <remarks>
    /// Bounded, and short. Failing to acquire it means another instance is already doing the work,
    /// which is success by a different route — waiting longer would only hold a connection open to
    /// watch somebody else finish.
    /// </remarks>
    [Range(1, 60)]
    public int LockTimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// Cross-property rules for <see cref="AuditPartitionOptions"/>.
/// </summary>
public sealed class AuditPartitionOptionsValidator : IValidateOptions<AuditPartitionOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditPartitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.RetentionMonths < AuditPartitionOptions.MinimumRetentionMonths)
        {
            // Unreachable through the range attribute, and stated anyway: the floor is a documented
            // compliance commitment (AU-7), not a tuning parameter, and a range attribute is easy
            // to widen without noticing what it was protecting.
            failures.Add(
                $"{AuditPartitionOptions.SectionName}:{nameof(AuditPartitionOptions.RetentionMonths)} " +
                $"is {options.RetentionMonths}; audit retention is documented as at least " +
                $"{AuditPartitionOptions.MinimumRetentionMonths} months (AU-7).");
        }

        // There is deliberately no rule relating the horizon to the retention period. An earlier
        // draft required the horizon to be shorter, on the theory that the job might otherwise
        // create and drop in the same cycle — but the two windows cannot overlap: partitions within
        // the horizon start at or after the current month, and an expired one ends at least
        // `RetentionMonths` before it. The rule added no safety and rejected the documented
        // defaults, which is the wrong trade for a validator that runs at startup.

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
