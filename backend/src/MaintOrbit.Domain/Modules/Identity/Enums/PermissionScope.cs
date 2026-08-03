namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// How far a role assignment reaches.
/// </summary>
/// <remarks>
/// <c>employee_roles.scope_type</c> (§4.2), evaluated alongside the permission itself — §3.5
/// describes scope as one of three dimensions assessed together, not a filter applied afterwards.
/// <para>
/// FR-PERM-007 adds that scope is evaluated <b>using only the current Company's data</b>. A Team
/// identifier from another Company can never satisfy a scope check, because the row naming it is
/// invisible under row-level security.
/// </para>
/// </remarks>
public enum PermissionScope
{
    /// <summary>Everything belonging to the Company.</summary>
    Company = 0,

    /// <summary>Only the assigned Team — the Team Lead's reach.</summary>
    Team = 1,

    /// <summary>Only the Employee's own records.</summary>
    Self = 2
}
