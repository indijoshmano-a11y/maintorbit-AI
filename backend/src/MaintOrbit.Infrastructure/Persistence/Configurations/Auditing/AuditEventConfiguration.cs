using System.Text.Json;
using AuditEvent = MaintOrbit.Domain.Modules.Auditing.Entities.AuditEvent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Auditing;

/// <summary>
/// Maps <see cref="AuditEvent"/> to <c>auditing.audit_events</c>.
/// </summary>
/// <remarks>
/// The relation is <b>partitioned by month and append-only</b>, and neither is expressible in an
/// EF model — so the migration creates the table with raw SQL while this configuration describes
/// the shape EF must agree with. The two are kept honest by
/// <c>AuditEventSchemaTests</c>, which reads the applied schema back and compares.
/// </remarks>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    /// <summary>Longest stored action name.</summary>
    internal const int ActionMaxLength = 128;

    /// <summary>Longest stored target type.</summary>
    internal const int TargetTypeMaxLength = 64;

    /// <summary>Longest stored target identifier.</summary>
    internal const int TargetIdMaxLength = 128;

    /// <summary>Longest stored correlation identifier.</summary>
    internal const int CorrelationIdMaxLength = 128;

    /// <summary>Longest stored stream entry identifier.</summary>
    internal const int StreamEntryIdMaxLength = 64;

    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditEvents", Schemas.Auditing);

        // Composite, because PostgreSQL requires the partition key in the primary key (DD-2).
        builder.HasKey(e => new { e.Id, e.OccurredAtUtc });

        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.OccurredAtUtc).IsRequired();

        builder.Property(e => e.Action).HasMaxLength(ActionMaxLength).IsRequired();

        // Stored as text, not as the enum's integer. An audit trail is read by auditors and
        // exported to customers (AU-6); a column of 0s and 1s that only a build can interpret
        // would make the export depend on a lookup nobody ships with it. The check constraints in
        // the migration keep the values closed.
        builder.Property(e => e.Outcome)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.ActorType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Nullable: a sign-in attempt against an unknown address belongs to no Company, because
        // the Company is the result of the lookup rather than an input to it.
        builder.Property(e => e.CompanyId);

        // A bare Guid, not an EmployeeId — that type belongs to `identity`, and this module may
        // not reference another module's internals (ADR-0002 R-5). The identifier crosses as a
        // value, exactly as the absent cross-schema foreign key does.
        builder.Property(e => e.ActorEmployeeId);

        builder.Property(e => e.TargetType).HasMaxLength(TargetTypeMaxLength);
        builder.Property(e => e.TargetId).HasMaxLength(TargetIdMaxLength);
        builder.Property(e => e.CorrelationId).HasMaxLength(CorrelationIdMaxLength);
        builder.Property(e => e.StreamEntryId).HasMaxLength(StreamEntryIdMaxLength);

        builder.Property(e => e.Context)
            .HasConversion(ContextConverter, ContextComparer)
            .HasColumnType("jsonb");

        // The four documented indexes, exactly (§4.10). All three composite ones lead with
        // company_id: every documented query is within one Company, and row-level security adds a
        // company_id predicate to each of them anyway, so a leading tenant column is what lets the
        // planner use the index rather than filter after it.
        // Carries the partition key alongside the column DD-6 names, because PostgreSQL refuses a
        // unique index on a partitioned table that omits it — DD-6 and DD-12 cannot both be
        // honoured literally. Redelivery replays the same stream entry as the same event at the
        // same instant, so the deduplication DD-6 exists for still works.
        builder.HasIndex(e => new { e.StreamEntryId, e.OccurredAtUtc })
            .IsUnique()
            .HasDatabaseName("ux_audit_events_stream_entry_id");

        builder.HasIndex(e => new { e.CompanyId, e.OccurredAtUtc })
            .HasDatabaseName("ix_audit_events_company_id_occurred_at_utc");

        builder.HasIndex(e => new { e.CompanyId, e.ActorEmployeeId, e.OccurredAtUtc })
            .HasDatabaseName("ix_audit_events_company_id_actor_employee_id_occurred_at_utc");

        builder.HasIndex(e => new { e.CompanyId, e.Action, e.OccurredAtUtc })
            .HasDatabaseName("ix_audit_events_company_id_action_occurred_at_utc");
    }

    /// <summary>
    /// Serializes the context to JSONB.
    /// </summary>
    /// <remarks>
    /// A plain dictionary rather than a typed document: §8.5 makes this the carrier for
    /// configuration before-and-after state, whose shape differs per action. What may go in it is
    /// constrained by <c>AuditContext.Sanitize</c> in the domain, not here — a converter is the
    /// wrong place for a security rule, because it only runs for callers who reach persistence.
    /// </remarks>
    private static readonly ValueConverter<IReadOnlyDictionary<string, string>?, string?>
        ContextConverter = new(
            value => value == null ? null : JsonSerializer.Serialize(value, JsonOptions),
            json => json == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)!);

    /// <summary>
    /// Compares contexts by content.
    /// </summary>
    /// <remarks>
    /// EF needs this to treat the dictionary as a value rather than by reference. On an
    /// append-only relation nothing is ever compared for modification, but without a comparer EF
    /// warns, and warnings are errors here.
    /// </remarks>
    private static readonly ValueComparer<IReadOnlyDictionary<string, string>?> ContextComparer =
        new(
            (left, right) => left == null
                ? right == null
                : right != null && left.Count == right.Count && !left.Except(right).Any(),
            value => value == null ? 0 : value.Count,
            value => value);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
