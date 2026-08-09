using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Application.Modules.Identity.Commands.Login;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Modules.Identity.Commands.SignIn;

/// <summary>
/// Signs an Employee in: authenticates them, opens a session, and issues both tokens.
/// </summary>
/// <remarks>
/// Composes rather than reimplements. Credential verification stays in
/// <see cref="LoginCommandHandler"/> — including its uniform failures and the decoy hash that
/// keeps an unknown address costing what a wrong password costs — and this adds only what a
/// session needs.
/// <para>
/// <b>The Company is resolved before anything else.</b> Row-level security means the credential
/// lookup finds nothing without a tenant in scope, and at sign-in the tenant is what is unknown.
/// <see cref="ICredentialDirectory"/> answers that one question, the scope opens, and everything
/// after it is filtered normally.
/// </para>
/// <para>
/// An unknown address still pays for a verification attempt. Returning early when the directory
/// finds no Company would make "no such address" faster than "wrong password" — the enumeration
/// oracle the login handler already closes, reopened one layer up.
/// </para>
/// </remarks>
public sealed class SignInCommandHandler(
    ICredentialDirectory directory,
    ITenantContext tenantContext,
    ICommandHandler<LoginCommand, AuthenticationResult> login,
    ISessionRepository sessions,
    IRefreshTokenRepository refreshTokens,
    IRefreshTokenFactory tokenFactory,
    IAccessTokenGenerator accessTokens,
    IUnitOfWork unitOfWork,
    IAuthenticationPolicyProvider policies,
    IOptions<RefreshTokenOptions> refreshOptions,
    IAuditTrail audit,
    TimeProvider timeProvider)
    : ICommandHandler<SignInCommand, SignInResult>
{
    private static Result<SignInResult> Rejected() =>
        Result.Failure<SignInResult>(
            Error.AuthenticationFailed("The email address or password is incorrect."));

    public async Task<Result<SignInResult>> HandleAsync(
        SignInCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Email.TryCreate(command.Email, out var email))
        {
            // A malformed address is a malformed request. No well-formed submission reaches here,
            // so returning early describes the input rather than any account.
            return Rejected();
        }

        var companyId = await directory.FindCompanyByEmailAsync(email, cancellationToken)
            .ConfigureAwait(false);

        if (companyId is null)
        {
            // No Company holds this address. The login handler is still invoked, under a scope that
            // matches nothing, so the request costs what a real attempt costs — see the remarks.
            await AuthenticateUnderNoTenantAsync(command, cancellationToken).ConfigureAwait(false);

            // FR-AUTH-014 audits failures as well as successes, and this is the one an attacker
            // generates: no Company, no Employee, and the attempted address recorded because an
            // audit record is not a response and enumeration is not a concern here.
            await AuditFailureAsync(command.Email, companyId: null, cancellationToken)
                .ConfigureAwait(false);

            return Rejected();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var authenticated = await login
            .HandleAsync(new LoginCommand(command.Email, command.Password), cancellationToken)
            .ConfigureAwait(false);

        if (authenticated.IsFailure)
        {
            await AuditFailureAsync(command.Email, companyId.Value, cancellationToken)
                .ConfigureAwait(false);

            return Rejected();
        }

        var identity = authenticated.Value;
        var now = timeProvider.GetUtcNow();

        // FR-AUTH-007 makes both session timers Company-configured. Read after authentication, so
        // the policy consulted is the one belonging to the Company the credential established —
        // and inside the tenant scope, so row-level security shows this Company's row and no other.
        var policy = await policies.GetAsync(identity.CompanyId, cancellationToken)
            .ConfigureAwait(false);

        var session = Session.Start(
            identity.CompanyId,
            identity.EmployeeId,
            command.ClientType,
            now,
            now.AddMinutes(policy.AbsoluteLifetimeMinutes),
            command.DeviceLabel,
            command.IpAddress);

        sessions.Add(session);

        var issued = tokenFactory.Issue();

        refreshTokens.Add(RefreshToken.IssueFirst(
            identity.CompanyId,
            session.Id,
            issued.Hash,
            now,
            now.AddMinutes(refreshOptions.Value.LifetimeMinutes)));

        var accessToken = accessTokens.Generate(identity.EmployeeId, identity.CompanyId, session.Id);

        // One command, one commit (§3.6). The session and its first refresh token must become
        // visible together: a session with no token cannot be refreshed, and a token with no
        // session authenticates nothing.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Built explicitly rather than through the ambient overload, for the same reason the
        // failure path below is: sign-in is an anonymous request, so there is no validated identity
        // for the trail to read. The identity exists here — it was just established — but only as a
        // local, and the ambient accessor is still empty.
        //
        // Using the ambient overload recorded a successful sign-in with no Company and an Anonymous
        // actor. That was invisible while the sink wrote to a log, and became a real defect the
        // moment events were persisted and tenant-scoped: the row belongs to no tenant, so the
        // Company can never see its own sign-ins — the records FR-AUTH-014 exists to give them.
        await audit.RecordAsync(
            new AuditEvent(
                now,
                AuditActions.SignIn,
                AuditOutcome.Success,
                AuditActorType.Employee,
                identity.CompanyId.Value,
                identity.EmployeeId.Value,
                AuditTargets.Session,
                session.Id.ToString(),
                CorrelationId: null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // The client type is the Employee's own descriptive field, not content (AU-4) —
                    // it is what makes a device list readable afterwards.
                    ["clientType"] = command.ClientType.ToString(),
                    ["employeeId"] = identity.EmployeeId.ToString()
                }),
            cancellationToken).ConfigureAwait(false);

        return Result.Success(new SignInResult(accessToken, issued.Token, session.Id));
    }

    /// <summary>
    /// Records a failed sign-in against whatever actor could be resolved.
    /// </summary>
    /// <remarks>
    /// Built explicitly rather than through the ambient overload: there is no validated identity at
    /// this point, so the trail has nothing to read. The attempted address is recorded because
    /// FR-AUTH-014 audits the attempt, and §3.4 makes a burst of them a detection signal — which
    /// only works if the records say which address was tried.
    /// </remarks>
    private Task AuditFailureAsync(
        string? attemptedEmail, CompanyId? companyId, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent(
                timeProvider.GetUtcNow(),
                AuditActions.SignIn,
                AuditOutcome.Failure,
                AuditActorType.Anonymous,
                companyId?.Value,
                ActorEmployeeId: null,
                AuditTargets.Employee,
                TargetId: null,
                CorrelationId: null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["attemptedEmail"] = attemptedEmail ?? string.Empty
                }),
            cancellationToken);

    /// <summary>
    /// Runs the credential check for an address no Company holds, and discards the outcome.
    /// </summary>
    /// <remarks>
    /// Only the elapsed work matters. The login handler finds no Employee under an empty tenant
    /// and verifies against its decoy hash, so this costs an Argon2id verification — the same as a
    /// wrong password against a real account.
    /// </remarks>
    private async Task AuthenticateUnderNoTenantAsync(
        SignInCommand command, CancellationToken cancellationToken)
    {
        await login
            .HandleAsync(new LoginCommand(command.Email, command.Password), cancellationToken)
            .ConfigureAwait(false);
    }
}
