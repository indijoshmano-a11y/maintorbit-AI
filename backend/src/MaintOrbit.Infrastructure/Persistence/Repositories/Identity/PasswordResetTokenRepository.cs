using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="IPasswordResetTokenRepository"/>.</summary>
internal sealed class PasswordResetTokenRepository(MaintOrbitDbContext context)
    : IPasswordResetTokenRepository
{
    /// <inheritdoc />
    public Task<PasswordResetToken?> FindByHashAsync(
        PasswordResetTokenHash hash, CancellationToken cancellationToken) =>
        // Tracked: the caller consumes the token it finds, and an untracked aggregate would be
        // marked consumed in memory and never written — which would leave every redeemed link
        // live for a second use, the one property FR-AUTH-012 turns on.
        context.PasswordResetTokens.FirstOrDefaultAsync(
            token => token.TokenHash == hash, cancellationToken);

    /// <inheritdoc />
    public void Add(PasswordResetToken token) => context.PasswordResetTokens.Add(token);

    /// <inheritdoc />
    public Task<int> InvalidateOutstandingForEmployeeAsync(
        EmployeeId employeeId,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken,
        PasswordResetTokenId? excluding = null) =>
        // Set-based, and it excludes rows already consumed or invalidated: a spent link is spent,
        // and overwriting its timestamps would lose whether it was used or superseded. Row-level
        // security still applies, so this reaches only the Company in scope.
        context.PasswordResetTokens
            .Where(token =>
                token.EmployeeId == employeeId &&
                token.ConsumedAtUtc == null &&
                token.InvalidatedAtUtc == null &&
                (excluding == null || token.Id != excluding.Value))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.InvalidatedAtUtc, invalidatedAtUtc),
                cancellationToken);
}
