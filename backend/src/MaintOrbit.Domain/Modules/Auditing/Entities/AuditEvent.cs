using MaintOrbit.Domain.Modules.Auditing.ValueObjects;
using MaintOrbit.Shared.Auditing;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Auditing.Entities;

/// <summary>
/// A compliance record of something that happened — C2, append-only.
/// </summary>
/// <remarks>
/// <b>This type has no mutating member, and that is the whole design.</b> AU-1 requires that no
/// update or delete path exists <i>in code</i> — "structural, not permission-based", as
/// <c>12-audit-and-compliance</c> §3.2 puts it. A permission can be misconfigured; an absent
/// method cannot be called. Every property is <c>private init</c>, the constructor is private, and
/// the only way to obtain an instance is <see cref="Record"/> or EF materialization.
/// <para>
/// The database says the same thing a second time — <c>REVOKE UPDATE, DELETE</c> (DD-11) and row
/// -level security policies that exist only for <c>SELECT</c> and <c>INSERT</c>. §8.2 calls that
/// "the belt to the code path's braces", and the two are deliberately independent: this class
/// cannot be edited into mutability without someone also editing a migration.
/// </para>
/// <para>
/// <b>Corrections are compensating rows, never edits</b> (§8.2). An audit record written in error
/// stays, and the correction is a second record. That is what makes immutability real rather than
/// nominal — a trail that can be tidied is a trail that can be laundered.
/// </para>
/// <para>
/// <b>Never contains prompt or completion content</b> (AU-4). <see cref="Context"/> carries
/// references and small scalars; the guard for what may go in it is
/// <see cref="AuditContext.Sanitize"/>, applied at construction rather than at the call site so
/// there is one place to audit rather than one per emission point.
/// </para>
/// </remarks>
public sealed class AuditEvent
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so an event can only come from <see cref="Record"/>. EF materializes through it,
    /// which correctly bypasses the invariants — a stored row satisfied them when it was written.
    /// </remarks>
    private AuditEvent()
    {
        Action = null!;
    }

    private AuditEvent(
        AuditEventId id,
        DateTimeOffset occurredAtUtc,
        string action,
        AuditOutcome outcome,
        AuditActorType actorType,
        CompanyId? companyId,
        Guid? actorEmployeeId,
        string? targetType,
        string? targetId,
        string? correlationId,
        IReadOnlyDictionary<string, string>? context)
    {
        Id = id;
        OccurredAtUtc = occurredAtUtc;
        Action = action;
        Outcome = outcome;
        ActorType = actorType;
        CompanyId = companyId;
        ActorEmployeeId = actorEmployeeId;
        TargetType = targetType;
        TargetId = targetId;
        CorrelationId = correlationId;
        Context = context;
    }

    /// <summary>Identifier of this event. Half of the primary key (DD-2).</summary>
    public AuditEventId Id { get; private init; }

    /// <summary>
    /// When the audited action happened (§1.7).
    /// </summary>
    /// <remarks>
    /// The partition key, and therefore the other half of the primary key — PostgreSQL requires
    /// the partition key to be part of it (DD-2). Monthly partitions make retention a partition
    /// drop (DB-P5) rather than a mass delete, which on an append-only relation is the only
    /// removal mechanism there is.
    /// </remarks>
    public DateTimeOffset OccurredAtUtc { get; private init; }

    /// <summary>
    /// What happened, from the <see cref="AuditActions"/> vocabulary.
    /// </summary>
    /// <remarks>
    /// Held as a string rather than an enum because the vocabulary is a published contract in
    /// Shared: <c>identity</c> emits against it without referencing this module (ADR-0002 R-5).
    /// The constants are the source of truth; a caller passing a literal is a defect an
    /// architecture rule catches.
    /// </remarks>
    public string Action { get; private init; }

    /// <summary>Whether the action succeeded, failed, or was denied.</summary>
    public AuditOutcome Outcome { get; private init; }

    /// <summary>Whether the actor was an Employee, the system, or unauthenticated.</summary>
    public AuditActorType ActorType { get; private init; }

    /// <summary>
    /// The Company this event belongs to, or <see langword="null"/> for a platform-level event.
    /// </summary>
    /// <remarks>
    /// <b>Nullable by necessity, not convenience.</b> A sign-in attempt for an address that
    /// matches no Employee has no tenant — the Company is the <i>result</i> of the lookup, not an
    /// input to it (<c>04-tenant-security</c> §3.4 path 13). Those attempts are the most
    /// security-relevant events the platform records, so refusing to store them would be the wrong
    /// trade.
    /// <para>
    /// Such rows are invisible to every tenant: the policy compares <c>company_id</c> to the
    /// session's Company, and <c>NULL = anything</c> is <c>NULL</c>. They are platform-level
    /// records, reachable only through an enumerated elevated path.
    /// </para>
    /// </remarks>
    public CompanyId? CompanyId { get; private init; }

    /// <summary>
    /// The Employee who acted, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Nullable **specifically to support pseudonymized erasure** (SD-018, DD-15) as well as
    /// anonymous and system actors: the record persists and the identity is cleared. Held as a
    /// raw <see cref="Guid"/> rather than an <c>EmployeeId</c> because that type belongs to
    /// <c>identity</c>, and this module may not reference another module's internals (ADR-0002
    /// R-5). The identifier crosses as a value, exactly like the schema's missing foreign key.
    /// </remarks>
    public Guid? ActorEmployeeId { get; private init; }

    /// <summary>What kind of thing was acted on, from <see cref="AuditTargets"/>.</summary>
    public string? TargetType { get; private init; }

    /// <summary>Which one.</summary>
    public string? TargetId { get; private init; }

    /// <summary>The request this event belongs to, for reconstruction across records.</summary>
    public string? CorrelationId { get; private init; }

    /// <summary>
    /// Additional detail, stored as JSONB.
    /// </summary>
    /// <remarks>
    /// Sanitized at construction. §8.5 makes this the carrier for configuration before-and-after
    /// state, which is why it is open-ended rather than a fixed column set — but open-ended is
    /// exactly what makes a credential-shaped key plausible, hence the guard.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Context { get; private init; }

    /// <summary>
    /// The stream entry this row was written from, for deduplication (DD-6).
    /// </summary>
    /// <remarks>
    /// <b>Always <see langword="null"/> today.</b> The column and its unique index exist because
    /// DD-6 specifies them and because §3.3's durable stream is the documented ingestion path —
    /// but that stream, its batch writer, and the reconciliation job are not built, so nothing
    /// has a stream entry to record. Emission writes straight through.
    /// <para>
    /// Kept rather than omitted because a unique index over all-<c>NULL</c> costs nothing —
    /// PostgreSQL treats nulls as distinct — while adding the column later to a populated,
    /// partitioned, append-only relation is the kind of change §9.2 warns is a full rewrite.
    /// </para>
    /// </remarks>
    public string? StreamEntryId { get; private init; }

    /// <summary>
    /// Records an event.
    /// </summary>
    /// <remarks>
    /// The only way to create one. Validates what AU-3 requires — actor, action, target, outcome,
    /// timestamp, originating context — and sanitizes the context before it can reach a column.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The action is absent, or the timestamp is unset.
    /// </exception>
    public static AuditEvent Record(
        DateTimeOffset occurredAtUtc,
        string action,
        AuditOutcome outcome,
        AuditActorType actorType,
        CompanyId? companyId = null,
        Guid? actorEmployeeId = null,
        string? targetType = null,
        string? targetId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            // An event that does not say what happened is not a record of anything, and it would
            // sit in an append-only relation forever being useless.
            throw new ArgumentException("An audit event must name an action.", nameof(action));
        }

        if (occurredAtUtc == default)
        {
            // The partition key. An unset timestamp routes the row to whatever partition covers
            // the epoch — or to none, which fails the insert far from the cause.
            throw new ArgumentException(
                "An audit event must record when it occurred.", nameof(occurredAtUtc));
        }

        if (actorType == AuditActorType.Employee && actorEmployeeId is null)
        {
            // Claiming an Employee acted while naming none makes the record unattributable and
            // contradicts itself. Anonymous and System actors legitimately have no identifier.
            throw new ArgumentException(
                "An Employee actor must be identified.", nameof(actorEmployeeId));
        }

        return new AuditEvent(
            AuditEventId.New(),
            occurredAtUtc,
            action,
            outcome,
            actorType,
            companyId,
            actorEmployeeId,
            targetType,
            targetId,
            correlationId,
            AuditContext.Sanitize(context));
    }
}
