using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A permission granted by a role.
/// </summary>
/// <remarks>
/// The join that makes a role a preset. Platform-wide alongside the catalogue and the role
/// definitions: what a role means is the same for every Company, and which Employee holds it is
/// what varies.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name is the documented table (database-design §4.2). The rule guards " +
                    "against confusion with the legacy System.Security.Permissions types, which " +
                    "this codebase does not use; renaming would put the model and the schema out " +
                    "of step for a suffix nobody is confused by.")]
public sealed class RolePermission
{
    private RolePermission()
    {
    }

    private RolePermission(RoleCode roleCode, PermissionCode permissionCode)
    {
        RoleCode = roleCode;
        PermissionCode = permissionCode;
    }

    /// <summary>The role.</summary>
    public RoleCode RoleCode { get; private init; }

    /// <summary>The permission it grants.</summary>
    public PermissionCode PermissionCode { get; private init; }

    /// <summary>Grants a permission to a role.</summary>
    public static RolePermission Grant(RoleCode roleCode, PermissionCode permissionCode)
    {
        if (roleCode.IsEmpty || permissionCode.IsEmpty)
        {
            throw new ArgumentException("A grant needs both a role and a permission.");
        }

        return new RolePermission(roleCode, permissionCode);
    }
}
