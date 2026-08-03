using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Api.Authorization;

/// <summary>
/// Declares the permission an endpoint requires.
/// </summary>
/// <remarks>
/// The whole authorization surface an endpoint sees. §3.7's policy-based model in one call:
/// the operation names the permission and scope it needs, and never mentions a role — SD-020
/// makes roles presets, and CLAUDE.md lists branching authorization on a role name under things
/// never to do.
/// </remarks>
public static class EndpointPermissionExtensions
{
    /// <summary>Requires a permission, at Company scope unless narrowed.</summary>
    public static TBuilder RequirePermission<TBuilder>(
        this TBuilder builder,
        PermissionCode permission,
        PermissionScope scope = PermissionScope.Company)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(PermissionRequirement.PolicyName(permission, scope));
    }
}
