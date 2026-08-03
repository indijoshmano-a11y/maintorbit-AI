using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Authorization;

/// <summary>
/// Decides whether the current caller may perform an operation.
/// </summary>
/// <remarks>
/// Named an evaluator rather than a service to avoid colliding with
/// <c>Microsoft.AspNetCore.Authorization.IAuthorizationService</c>, which the API layer also uses
/// and which answers a different question — that one evaluates policies against a principal, this
/// one evaluates a permission against the database.
/// <para>
/// It takes the identity from the ambient current-identity accessor rather than as a parameter.
/// TC-1 and §3.6 both derive the caller server-side, and a caller-supplied Employee identifier
/// here would be a caller choosing whose permissions to be checked against.
/// </para>
/// </remarks>
public interface IAuthorizationEvaluator
{
    /// <summary>
    /// Whether the authenticated caller holds a permission at a sufficient scope.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for an unauthenticated request. Deny by default means the
    /// absence of an identity is refusal, not an error to surface differently.
    /// </remarks>
    Task<bool> IsGrantedAsync(
        PermissionCode permission,
        PermissionScope requiredScope,
        Guid? target,
        CancellationToken cancellationToken);
}
