using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;

namespace MaintOrbit.Application.Modules.Identity.Commands.AuthenticationPolicy;

/// <summary>The Company's authentication policy as the API reports it.</summary>
public sealed record AuthenticationPolicyView(
    int MinimumPasswordLength,
    bool RequireBreachCheck,
    int IdleTimeoutMinutes,
    int AbsoluteLifetimeMinutes,
    bool MfaRequired,
    int MaximumFailedAttempts,
    int LockoutMinutes,
    bool IsCompanyConfigured);

/// <summary>Reads the caller's own Company's policy.</summary>
/// <remarks>
/// No Company parameter: §3.3 makes the Company path singular because "a caller has exactly one
/// Company", and TC-1 derives it from the credential.
/// </remarks>
public sealed record GetAuthenticationPolicyQuery : IQuery<AuthenticationPolicyView>;

/// <summary>Replaces the caller's own Company's policy.</summary>
/// <remarks>
/// Whole-policy replacement rather than a patch. The rules are relational — the absolute lifetime
/// must not be shorter than the idle window — and a partial update would refuse a legitimate pair
/// depending on which half arrived first.
/// </remarks>
public sealed record UpdateAuthenticationPolicyCommand(
    int MinimumPasswordLength,
    bool RequireBreachCheck,
    int IdleTimeoutMinutes,
    int AbsoluteLifetimeMinutes,
    bool MfaRequired,
    int MaximumFailedAttempts,
    int LockoutMinutes) : ICommand<AuthenticationPolicyView>;

/// <summary>Returns the policy in force, configured or defaulted.</summary>
public sealed class GetAuthenticationPolicyQueryHandler(
    ICurrentIdentity currentIdentity,
    ICompanyAuthenticationPolicyRepository policies,
    IAuthenticationPolicyProvider provider)
    : IQueryHandler<GetAuthenticationPolicyQuery, AuthenticationPolicyView>
{
    public async Task<Result<AuthenticationPolicyView>> HandleAsync(
        GetAuthenticationPolicyQuery query, CancellationToken cancellationToken)
    {
        var companyId = currentIdentity.RequireCompanyId();

        var policy = await provider.GetAsync(companyId, cancellationToken).ConfigureAwait(false);

        // Reported so an administrator can tell "we chose these" from "nobody has chosen". The
        // values are identical either way, which is the point — but which of the two it is
        // determines whether a deployment default change would move them.
        var configured = await policies.FindAsync(companyId, cancellationToken)
            .ConfigureAwait(false) is not null;

        return Result.Success(View(policy, configured));
    }

    internal static AuthenticationPolicyView View(
        CompanyAuthenticationPolicy policy, bool isCompanyConfigured) =>
        new(policy.MinimumPasswordLength,
            policy.RequireBreachCheck,
            policy.IdleTimeoutMinutes,
            policy.AbsoluteLifetimeMinutes,
            policy.MfaRequired,
            policy.MaximumFailedAttempts,
            policy.LockoutMinutes,
            isCompanyConfigured);
}

/// <summary>
/// Sets the Company's policy, creating the row the first time.
/// </summary>
/// <remarks>
/// One command, one commit. The aggregate owns every bound, so this handler decides only whether a
/// row exists — and the database repeats the bounds as check constraints, because a policy is read
/// by code that trusts it.
/// <para>
/// §3.7 notes that "changes to authentication policy require step-up authentication". Step-up
/// enforcement does not exist yet; this endpoint holds <c>company.manage [C]</c> and no more, which
/// is recorded as outstanding rather than assumed to be sufficient.
/// </para>
/// </remarks>
public sealed class UpdateAuthenticationPolicyCommandHandler(
    ICurrentIdentity currentIdentity,
    ICompanyAuthenticationPolicyRepository policies,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<UpdateAuthenticationPolicyCommand, AuthenticationPolicyView>
{
    public async Task<Result<AuthenticationPolicyView>> HandleAsync(
        UpdateAuthenticationPolicyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var companyId = currentIdentity.RequireCompanyId();
        var employeeId = currentIdentity.RequireEmployeeId();
        var now = timeProvider.GetUtcNow();

        var existing = await policies.FindAsync(companyId, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            var created = CompanyAuthenticationPolicy.Create(
                companyId,
                command.MinimumPasswordLength,
                command.RequireBreachCheck,
                command.IdleTimeoutMinutes,
                command.AbsoluteLifetimeMinutes,
                command.MfaRequired,
                command.MaximumFailedAttempts,
                command.LockoutMinutes,
                now);

            if (created.IsFailure)
            {
                return Result.Failure<AuthenticationPolicyView>(created.Error);
            }

            policies.Add(created.Value);

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success(
                GetAuthenticationPolicyQueryHandler.View(created.Value, isCompanyConfigured: true));
        }

        var updated = existing.Update(
            command.MinimumPasswordLength,
            command.RequireBreachCheck,
            command.IdleTimeoutMinutes,
            command.AbsoluteLifetimeMinutes,
            command.MfaRequired,
            command.MaximumFailedAttempts,
            command.LockoutMinutes,
            now,
            employeeId);

        if (updated.IsFailure)
        {
            // Nothing is committed, so the rejected values never reach the row — the aggregate
            // refused before mutating, which is why Update validates first.
            return Result.Failure<AuthenticationPolicyView>(updated.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(
            GetAuthenticationPolicyQueryHandler.View(existing, isCompanyConfigured: true));
    }
}
