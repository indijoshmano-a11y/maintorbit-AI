using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="ICompanyAuthenticationPolicyRepository"/>.</summary>
internal sealed class CompanyAuthenticationPolicyRepository(MaintOrbitDbContext context)
    : ICompanyAuthenticationPolicyRepository
{
    /// <inheritdoc />
    public Task<CompanyAuthenticationPolicy?> FindAsync(
        CompanyId companyId, CancellationToken cancellationToken) =>
        // Tracked: the update path changes what it finds. The read path pays a little for that,
        // and a second untracked method would be two ways to load one row — which is how the two
        // end up filtering differently.
        context.CompanyAuthenticationPolicies.FirstOrDefaultAsync(
            policy => policy.CompanyId == companyId, cancellationToken);

    /// <inheritdoc />
    public void Add(CompanyAuthenticationPolicy policy) =>
        context.CompanyAuthenticationPolicies.Add(policy);
}
