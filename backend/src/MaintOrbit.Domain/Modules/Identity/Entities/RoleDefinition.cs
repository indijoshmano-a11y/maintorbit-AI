using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A named set of permissions.
/// </summary>
/// <remarks>
/// §4.2: "the seven fixed roles now and customer-composed roles at v2.0 (FR-PERM-006)". Modelling
/// them as rows rather than an enum is what makes custom roles "a data change rather than a
/// rewrite".
/// <para>
/// <b>There is no hierarchy.</b> §3.4 is explicit that Owner does not inherit Company Admin, and
/// that Billing Admin and Developer are incomparable — each holds permissions the other lacks. A
/// linear ordering would grant Billing Admin access to provider configuration it has no business
/// seeing.
/// </para>
/// </remarks>
public sealed class RoleDefinition
{
    private RoleDefinition()
    {
        Name = null!;
    }

    private RoleDefinition(RoleCode code, string name, bool isBuiltIn)
    {
        Code = code;
        Name = name;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>The code — the primary key, and what <c>invitations.role_code</c> refers to.</summary>
    public RoleCode Code { get; private init; }

    /// <summary>Display name.</summary>
    public string Name { get; private set; }

    /// <summary>
    /// Whether this is one of the seven fixed roles.
    /// </summary>
    /// <remarks>
    /// Distinguishes a role the platform ships from one a Company composes at v2.0. It marks
    /// origin; it confers nothing, and nothing branches on it to decide access.
    /// </remarks>
    public bool IsBuiltIn { get; private init; }

    /// <summary>Defines a role.</summary>
    public static RoleDefinition Define(RoleCode code, string name, bool isBuiltIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (code.IsEmpty)
        {
            throw new ArgumentException("A role needs a code.", nameof(code));
        }

        return new RoleDefinition(code, name, isBuiltIn);
    }
}
