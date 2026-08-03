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
    IOptions<SessionOptions> sessionOptions,
    IOptions<RefreshTokenOptions> refreshOptions,
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
            return Rejected();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var authenticated = await login
            .HandleAsync(new LoginCommand(command.Email, command.Password), cancellationToken)
            .ConfigureAwait(false);

        if (authenticated.IsFailure)
        {
            return Rejected();
        }

        var identity = authenticated.Value;
        var now = timeProvider.GetUtcNow();

        var session = Session.Start(
            identity.CompanyId,
            identity.EmployeeId,
            command.ClientType,
            now,
            now.AddMinutes(sessionOptions.Value.AbsoluteLifetimeMinutes),
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

        return Result.Success(new SignInResult(accessToken, issued.Token, session.Id));
    }

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
