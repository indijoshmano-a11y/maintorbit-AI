namespace MaintOrbit.Domain.Modules.Auditing.ValueObjects;

/// <summary>
/// Identifier of an Audit Event.
/// </summary>
/// <remarks>
/// UUIDv7 (§1.6). On a partitioned, append-only, write-heavy relation this matters more than
/// elsewhere: a random v4 scatters B-tree inserts across the whole index, and §9.4 names index
/// maintenance on ledger inserts as the fifth bottleneck the system meets. Time-ordered keys keep
/// inserts at the right-hand edge of the index.
/// </remarks>
public readonly record struct AuditEventId(Guid Value)
{
    /// <summary>The unset identifier.</summary>
    public static AuditEventId Empty => default;

    /// <summary>Whether the identifier is unset.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static AuditEventId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("n");
}
