using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Authorization;

/// <summary>
/// Builds a policy for any permission an endpoint declares.
/// </summary>
/// <remarks>
/// Without this, every permission would need registering by hand at startup — a list that drifts
/// from the endpoints it serves, and whose omissions surface as "policy not found" at runtime.
/// Deriving the policy from its name means declaring a permission on an endpoint is all there is
/// to do.
/// <para>
/// Anything that is not a permission policy falls through to the default provider, so the
/// framework's own conventions keep working.
/// </para>
/// </remarks>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (PermissionRequirement.TryParse(policyName) is { } requirement)
        {
            var policy = new AuthorizationPolicyBuilder()
                // An authenticated caller first: a permission check on an anonymous request would
                // resolve nothing and deny, but saying so as an authentication failure is the more
                // accurate answer and produces a 401 rather than a 403.
                .RequireAuthenticatedUser()
                .AddRequirements(requirement)
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
