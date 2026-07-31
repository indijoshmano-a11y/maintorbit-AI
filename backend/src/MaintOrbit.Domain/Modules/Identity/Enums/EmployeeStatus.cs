namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// Where an Employee sits in their lifecycle.
/// </summary>
/// <remarks>
/// The four states named in database-design §4.2. No others exist, and the set is closed by a
/// check constraint in the database as well as by this type — the constraint is what holds when a
/// row is written by anything other than this application.
/// <para>
/// This is a lifecycle state, <b>not</b> an authorization input. Roles are presets and
/// permissions are evaluated; nothing branches on a status or a role name to decide access
/// (05-security §7–8).
/// </para>
/// </remarks>
public enum EmployeeStatus
{
    /// <summary>Invited, but has not yet completed sign-up.</summary>
    Invited = 0,

    /// <summary>Able to authenticate and use the platform.</summary>
    Active = 1,

    /// <summary>Access withdrawn, record intact and restorable.</summary>
    Suspended = 2,

    /// <summary>
    /// Removed from the Company.
    /// </summary>
    /// <remarks>
    /// Distinct from soft deletion. §1.8 keeps the row so ledger and audit attribution survives —
    /// a removed Employee's past Usage Records must still name who incurred them.
    /// </remarks>
    Removed = 3
}
