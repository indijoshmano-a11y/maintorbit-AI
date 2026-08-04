using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="CompanyAuthenticationPolicy"/>.</summary>
/// <remarks>
/// Tenant filtering is absent for the same reason as every other repository here: row-level
/// security applies it below the application layer (ADR-0005), and a second discretionary copy is
/// the one that gets forgotten.
/// </remarks>
public interface ICompanyAuthenticationPolicyRepository
{
    /// <summary>
    /// The Company's policy, or <see langword="null"/> if it has never set one.
    /// </summary>
    /// <remarks>
    /// Null means "the deployment defaults apply", not "unconfigured" — the caller resolves that
    /// through <see cref="CompanyAuthenticationPolicy.Default"/>, so no reader has to decide what
    /// an absent policy means.
    /// </remarks>
    Task<CompanyAuthenticationPolicy?> FindAsync(
        CompanyId companyId, CancellationToken cancellationToken);

    /// <summary>Adds a policy to the unit of work.</summary>
    void Add(CompanyAuthenticationPolicy policy);
}
