using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="RefreshToken"/> aggregates.</summary>
public interface IRefreshTokenRepository
{
    /// <summary>
    /// Finds a token by its hash, or <see langword="null"/> if no such token exists.
    /// </summary>
    /// <remarks>
    /// Lookup is by hash because the plaintext is never stored — the caller hashes what it was
    /// presented and looks for a match. <c>ux_refresh_tokens_token_hash</c> makes this a single
    /// index probe.
    /// <para>
    /// Returns used and revoked tokens too. A caller must be able to tell "already consumed" from
    /// "never existed": the first is reuse and revokes a family, the second is nothing.
    /// </para>
    /// </remarks>
    Task<RefreshToken?> FindByHashAsync(RefreshTokenHash hash, CancellationToken cancellationToken);

    /// <summary>Adds a newly issued token to the unit of work.</summary>
    void Add(RefreshToken token);

    /// <summary>
    /// Revokes every token in a family.
    /// </summary>
    /// <remarks>
    /// SD-014's response to reuse. Applied to the whole family, including tokens already consumed,
    /// because a consumed token that is revoked can no longer be part of a chain an attacker
    /// continues.
    /// </remarks>
    Task<int> RevokeFamilyAsync(
        RefreshTokenFamilyId familyId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);
}
