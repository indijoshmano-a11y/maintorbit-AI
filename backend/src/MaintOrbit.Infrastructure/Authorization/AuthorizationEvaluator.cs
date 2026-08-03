using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Infrastructure.Authorization;

/// <summary>
/// Evaluates a permission against the current caller.
/// </summary>
/// <remarks>
/// Takes the identity from <see cref="ICurrentIdentity"/>, which reads the validated token — so
/// the Employee and Company being checked are the ones the credential established, not ones a
/// request named.
/// </remarks>
internal sealed class AuthorizationEvaluator(
    ICurrentIdentity currentIdentity,
    IPermissionService permissions)
    : IAuthorizationEvaluator
{
    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(
        PermissionCode permission,
        PermissionScope requiredScope,
        Guid? target,
        CancellationToken cancellationToken)
    {
        if (currentIdentity is { EmployeeId: { } employeeId, CompanyId: { } companyId })
        {
            var held = await permissions
                .ResolveAsync(employeeId, companyId, cancellationToken).ConfigureAwait(false);

            return held.IsGranted(permission, requiredScope, target);
        }

        // Unauthenticated. Deny by default makes this a refusal rather than an error — the caller
        // is told they may not, which is true.
        return false;
    }
}
