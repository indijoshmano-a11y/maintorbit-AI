using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Common.Authorization;

/// <summary>
/// The <c>auditing</c> module's permission codes.
/// </summary>
/// <remarks>
/// A second catalogue rather than an addition to <see cref="IdentityPermissions"/>: a permission
/// belongs to the module whose resource it governs, and `audit.read` governs
/// <c>auditing.audit_events</c>. The architecture rule that mattered — a code is named in exactly
/// one place, so a typo is a compile error rather than a policy that silently denies everybody —
/// is preserved and now checked across both files.
/// </remarks>
public static class AuditPermissions
{
    /// <summary>
    /// Reading and exporting Audit Events.
    /// </summary>
    /// <remarks>
    /// <c>api-specification</c> §3.15 names it exactly: <c>audit.read [C]</c>, held by Owner,
    /// Company Admin, and Auditor only. Company scope, because an audit trail restricted to the
    /// reader's own actions would answer none of the questions it exists for.
    /// <para>
    /// <b>One permission covers search and export.</b> §3.15 lists both under the same code, and
    /// that is right: export is search with a different transport, and a caller who can page
    /// through every record can already assemble the same file by hand.
    /// </para>
    /// </remarks>
    public static PermissionCode AuditRead { get; } = PermissionCode.Create("audit.read");
}
