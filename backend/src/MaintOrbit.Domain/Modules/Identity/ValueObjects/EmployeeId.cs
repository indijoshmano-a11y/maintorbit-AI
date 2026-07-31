namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// Identifies an Employee.
/// </summary>
/// <remarks>
/// UUIDv7 per database-design §1.6: time-ordered, so index inserts stay local to the right-hand
/// edge of the B-tree instead of scattering the way UUIDv4 does. The same value is the external
/// identifier — §1.6 states there is no separate public identifier — so it must also be
/// unpredictable, which UUIDv7 satisfies through its random component (threat I-13).
/// <para>
/// <b>Generated in the application, not the database.</b> TD-5 is open: PostgreSQL 18 provides a
/// native UUIDv7 generator and 17 does not. Generating here works against both, and is the only
/// choice that does not have to be revisited when TD-5 lands.
/// </para>
/// </remarks>
public readonly record struct EmployeeId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static EmployeeId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static EmployeeId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
