using System.Globalization;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.AspNetCore.Authorization;

namespace MaintOrbit.Api.Authorization;

/// <summary>
/// The permission an endpoint requires.
/// </summary>
/// <remarks>
/// §3.7 describes authorization as policy-based: "the operation declares the permission and scope
/// it requires". This is that declaration — the endpoint states what is needed, and the handler
/// decides whether the caller has it. Nothing anywhere states a <i>role</i>.
/// </remarks>
public sealed class PermissionRequirement(PermissionCode permission, PermissionScope scope)
    : IAuthorizationRequirement
{
    /// <summary>The permission required.</summary>
    public PermissionCode Permission { get; } = permission;

    /// <summary>The scope at which it must be held.</summary>
    public PermissionScope Scope { get; } = scope;

    /// <summary>
    /// The policy name encoding this requirement.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core identifies policies by string, so a requirement has to survive a round trip
    /// through one. Encoding it means endpoints declare a permission rather than a policy someone
    /// remembered to register, and a typo produces a policy that denies rather than one that is
    /// missing — which under deny-by-default is the same safe outcome either way.
    /// </remarks>
    public const string Prefix = "permission:";

    /// <summary>Builds the policy name for a permission and scope.</summary>
    public static string PolicyName(PermissionCode permission, PermissionScope scope) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{permission.Value}:{scope}");

    /// <summary>Parses a policy name back into a requirement, or null if it is not one.</summary>
    public static PermissionRequirement? TryParse(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = policyName[Prefix.Length..].Split(':');

        if (parts.Length != 2
            || !PermissionCode.TryCreate(parts[0], out var permission)
            || !Enum.TryParse<PermissionScope>(parts[1], out var scope))
        {
            return null;
        }

        return new PermissionRequirement(permission, scope);
    }
}
