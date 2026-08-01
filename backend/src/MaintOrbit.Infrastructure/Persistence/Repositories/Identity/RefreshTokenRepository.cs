using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="IRefreshTokenRepository"/>.</summary>
internal sealed class RefreshTokenRepository(MaintOrbitDbContext context) : IRefreshTokenRepository
{
    /// <inheritdoc />
    public Task<RefreshToken?> FindByHashAsync(
        RefreshTokenHash hash, CancellationToken cancellationToken) =>
        // Tracked: the caller consumes the token it finds, and an untracked aggregate would be
        // marked used in memory and never written — which would turn every rotation into a
        // silently unconsumed token, and every legitimate refresh into undetected reuse.
        context.RefreshTokens.FirstOrDefaultAsync(
            token => token.TokenHash == hash, cancellationToken);

    /// <inheritdoc />
    public void Add(RefreshToken token) => context.RefreshTokens.Add(token);

    /// <inheritdoc />
    public Task<int> RevokeFamilyAsync(
        RefreshTokenFamilyId familyId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        // Set-based, and deliberately includes tokens already consumed: a used token that is also
        // revoked cannot become part of a chain an attacker continues.
        context.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
}
