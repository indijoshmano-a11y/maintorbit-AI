using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Resolves a Company's authentication policy, falling back to the deployment defaults.
/// </summary>
/// <remarks>
/// <b>The fallback is the whole reason this exists.</b> Every caller on the authentication path
/// would otherwise have to know that an absent row means the defaults, and one that forgets is one
/// Company running with whatever the caller decided null should mean.
/// <para>
/// The lookup is tenant-scoped like every other read: row-level security filters the table, so a
/// policy belonging to another Company is invisible even to a resolver asked for it by identifier.
/// </para>
/// </remarks>
internal sealed class AuthenticationPolicyProvider(
    ICompanyAuthenticationPolicyRepository policies,
    IOptions<AuthenticationPolicyDefaults> defaults)
    : IAuthenticationPolicyProvider
{
    /// <inheritdoc />
    public async Task<CompanyAuthenticationPolicy> GetAsync(
        CompanyId companyId, CancellationToken cancellationToken)
    {
        var stored = await policies.FindAsync(companyId, cancellationToken).ConfigureAwait(false);

        if (stored is not null)
        {
            return stored;
        }

        var value = defaults.Value;

        // Built from the aggregate, so the defaults pass the same rules a saved policy does. The
        // startup validator has already established that they do, which makes this construction
        // unable to fail — and the fallback below is what happens if that ever stops being true.
        var fallback = CompanyAuthenticationPolicy.Create(
            companyId,
            value.MinimumPasswordLength,
            value.RequireBreachCheck,
            value.IdleTimeoutMinutes,
            value.AbsoluteLifetimeMinutes,
            value.MfaRequired,
            value.MaximumFailedAttempts,
            value.LockoutMinutes,
            DateTimeOffset.UnixEpoch);

        // The platform's own defaults, not the caller's. Falling back to something weaker here
        // would turn a configuration fault into a quietly relaxed policy.
        return fallback.IsSuccess ? fallback.Value : CompanyAuthenticationPolicy.Default(companyId);
    }
}
