using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Resolves the authentication policy in force for a Company.
/// </summary>
/// <remarks>
/// A port because the policy is read on the authentication path — sign-in, invitation acceptance,
/// password reset — and each of those would otherwise have to know that an absent row means the
/// deployment defaults. One reader that forgets is one Company running an unbounded session.
/// <para>
/// <b>It never returns null.</b> A Company that has not set a policy has one; the difference is
/// only whether a row exists.
/// </para>
/// </remarks>
public interface IAuthenticationPolicyProvider
{
    /// <summary>The policy in force, falling back to the deployment defaults.</summary>
    Task<CompanyAuthenticationPolicy> GetAsync(
        CompanyId companyId, CancellationToken cancellationToken);
}
