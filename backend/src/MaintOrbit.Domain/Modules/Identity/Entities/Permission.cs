using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// One atomic permission in the catalogue.
/// </summary>
/// <remarks>
/// §4.2: "<c>permissions</c> is the atomic catalogue". Platform-wide reference data, not
/// tenant-scoped — the set of things that <i>can</i> be permitted is the same for every Company;
/// which of them an Employee holds is what varies, and that lives in <c>employee_roles</c>.
/// <para>
/// It therefore carries no <c>company_id</c> and no row-level security policy, which is the one
/// deliberate exception to DB-P1 in this schema. A catalogue row names a capability and grants
/// nothing.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name is the documented table (database-design §4.2). The rule guards " +
                    "against confusion with the legacy System.Security.Permissions types, which " +
                    "this codebase does not use; renaming would put the model and the schema out " +
                    "of step for a suffix nobody is confused by.")]
public sealed class Permission
{
    private Permission()
    {
        Description = null!;
    }

    private Permission(PermissionCode code, string description)
    {
        Code = code;
        Description = description;
    }

    /// <summary>The code — the primary key (§1.6 reference data).</summary>
    public PermissionCode Code { get; private init; }

    /// <summary>What holding this permission allows.</summary>
    /// <remarks>Shown when composing a custom role at v2.0 (FR-PERM-006).</remarks>
    public string Description { get; private set; }

    /// <summary>Adds a permission to the catalogue.</summary>
    public static Permission Define(PermissionCode code, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (code.IsEmpty)
        {
            throw new ArgumentException("A permission needs a code.", nameof(code));
        }

        return new Permission(code, description);
    }
}
