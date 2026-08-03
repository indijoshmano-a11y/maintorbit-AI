using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;

/// <summary>Resolves the tenant for a refresh request, then rotates.</summary>
/// <remarks>
/// A refresh request arrives with nothing but a token, so it has the same chicken-and-egg problem
/// as sign-in: the token lookup is tenant-scoped and the tenant is what the token is meant to
/// establish. <see cref="ICredentialDirectory"/> answers only "which Company", the scope opens,
/// and rotation — including reuse detection and family revocation — runs unchanged inside it.
/// <para>
/// This adds no rotation logic. It exists so <see cref="RotateRefreshTokenCommandHandler"/> can
/// stay a handler that assumes a tenant, the way every other handler does.
/// </para>
/// </remarks>
public sealed class RefreshSessionCommandHandler(
    ICredentialDirectory directory,
    ITenantContext tenantContext,
    IRefreshTokenFactory tokenFactory,
    ICommandHandler<RotateRefreshTokenCommand, RefreshedTokens> rotate)
    : ICommandHandler<RefreshSessionCommand, RefreshedTokens>
{
    private static Result<RefreshedTokens> Rejected() =>
        Result.Failure<RefreshedTokens>(
            Error.AuthenticationFailed("The refresh token is not valid."));

    public async Task<Result<RefreshedTokens>> HandleAsync(
        RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Rejected();
        }

        var hash = tokenFactory.Hash(command.RefreshToken);

        var companyId = await directory
            .FindCompanyByRefreshTokenAsync(hash, cancellationToken).ConfigureAwait(false);

        if (companyId is null)
        {
            // No such token in any Company. Indistinguishable from every other refresh failure.
            return Rejected();
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        return await rotate
            .HandleAsync(new RotateRefreshTokenCommand(command.RefreshToken), cancellationToken)
            .ConfigureAwait(false);
    }
}
