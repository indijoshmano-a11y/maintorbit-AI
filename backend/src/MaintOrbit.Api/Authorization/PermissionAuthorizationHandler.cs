using Microsoft.AspNetCore.Authorization;

// Disambiguated from Microsoft.AspNetCore.Authorization.IAuthorizationEvaluator, which evaluates
// policy results rather than permissions.
using PermissionEvaluator = MaintOrbit.Application.Abstractions.Authorization.IAuthorizationEvaluator;

namespace MaintOrbit.Api.Authorization;

/// <summary>
/// Decides a <see cref="PermissionRequirement"/> against the caller's resolved permissions.
/// </summary>
/// <remarks>
/// The bridge between ASP.NET Core's policy machinery and the permission model. It reads nothing
/// from the principal beyond the fact that one exists — the permissions come from the database
/// through <see cref="IAuthorizationEvaluator"/>, because FR-PERM-005 requires a role change
/// effective within 60 seconds and a claim in a 15-minute token cannot honour that.
/// <para>
/// It never calls <c>Fail</c>. Not calling <c>Succeed</c> is already a denial under
/// deny-by-default, and an explicit failure would veto any other requirement that might have
/// granted the request — which is not this handler's decision to make.
/// </para>
/// </remarks>
internal sealed class PermissionAuthorizationHandler(PermissionEvaluator evaluator)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // The scope target — which Team, which Employee — is a property of the operation, and the
        // endpoint that knows it is not yet built. Resolving it from the route is the next
        // milestone's work; until then a Team-scoped requirement can only be satisfied by a
        // Company-wide grant, which is the conservative direction.
        var granted = await evaluator
            .IsGrantedAsync(requirement.Permission, requirement.Scope, target: null, CancellationToken.None)
            .ConfigureAwait(false);

        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}
