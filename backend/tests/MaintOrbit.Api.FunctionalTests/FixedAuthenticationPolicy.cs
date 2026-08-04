using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// A policy provider that answers with one policy, for tests that are not about the policy.
/// </summary>
/// <remarks>
/// The handlers that set a password and open a session now read the Company's policy, so a test
/// exercising those paths needs one. Substituting the aggregate's own defaults keeps those tests
/// about what they were about — the policy's <i>own</i> behaviour is covered end to end against a
/// real database.
/// </remarks>
internal sealed class FixedAuthenticationPolicy(CompanyAuthenticationPolicy? policy = null)
    : IAuthenticationPolicyProvider
{
    public Task<CompanyAuthenticationPolicy> GetAsync(
        CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult(policy ?? CompanyAuthenticationPolicy.Default(companyId));
}
