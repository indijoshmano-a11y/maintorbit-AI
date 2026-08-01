using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A single-use refresh token — C4 data.
/// </summary>
/// <remarks>
/// SD-014 rotates on every use, so one authentication produces a chain of these, each superseding
/// the last and all sharing a <see cref="FamilyId"/>. The chain is what makes theft detectable:
/// the legitimate client and an attacker inevitably both present the same token, and whichever
/// arrives second finds <see cref="UsedAtUtc"/> already set.
/// <para>
/// <b>Used tokens are not deleted.</b> §4.2 is explicit that reuse detection depends on
/// recognising an already-consumed token, so they are retained for the session's absolute lifetime
/// plus a margin and then purged. Deleting on use would turn a replay into "unknown token", which
/// is indistinguishable from a typo and triggers nothing.
/// </para>
/// <para>
/// Only the hash is stored. The token itself exists once, on its way to the client
/// (<see cref="RefreshTokenHash"/>).
/// </para>
/// </remarks>
public sealed class RefreshToken
{
    /// <summary>Constructor for the persistence layer.</summary>
    private RefreshToken() => TokenHash = null!;

    private RefreshToken(
        RefreshTokenId id,
        CompanyId companyId,
        SessionId sessionId,
        RefreshTokenFamilyId familyId,
        RefreshTokenHash tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        SessionId = sessionId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Identifier of this token record.</summary>
    public RefreshTokenId Id { get; private init; }

    /// <summary>The Company this token belongs to — the tenant discriminator (DB-P1).</summary>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The device session this token is bound to (SD-014, SD-016).</summary>
    /// <remarks>The binding that makes per-device revocation possible.</remarks>
    public SessionId SessionId { get; private init; }

    /// <summary>The rotation chain this token belongs to.</summary>
    /// <remarks>Reuse revokes the whole family, not just the token that was replayed.</remarks>
    public RefreshTokenFamilyId FamilyId { get; private init; }

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public RefreshTokenHash TokenHash { get; private init; }

    /// <summary>When the token was issued (§1.7).</summary>
    public DateTimeOffset IssuedAtUtc { get; private init; }

    /// <summary>
    /// When the token stops being accepted.
    /// </summary>
    /// <remarks>
    /// Not in §4.2's key column list. It is carried because a token outliving its session would
    /// otherwise be bounded only by the session lookup, and a token row that can never be accepted
    /// is one the purge can recognise without joining.
    /// </remarks>
    public DateTimeOffset ExpiresAtUtc { get; private init; }

    /// <summary>
    /// When the token was consumed, or <see langword="null"/> if it has not been.
    /// </summary>
    /// <remarks>
    /// Half of the reuse-detection mechanism. A token presented with this already set is being
    /// replayed.
    /// </remarks>
    public DateTimeOffset? UsedAtUtc { get; private set; }

    /// <summary>The token issued in this one's place, forming the rotation chain.</summary>
    public RefreshTokenId? SupersededById { get; private set; }

    /// <summary>When the token was revoked, or <see langword="null"/> if it was not.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the token has already been consumed.</summary>
    public bool IsUsed => UsedAtUtc is not null;

    /// <summary>Whether the token has been revoked.</summary>
    public bool IsRevoked => RevokedAtUtc is not null;

    /// <summary>
    /// Issues the first token of a new family, at authentication.
    /// </summary>
    public static RefreshToken IssueFirst(
        CompanyId companyId,
        SessionId sessionId,
        RefreshTokenHash tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc) =>
        Issue(companyId, sessionId, RefreshTokenFamilyId.New(), tokenHash, issuedAtUtc, expiresAtUtc);

    /// <summary>
    /// Issues a token into an existing family, replacing one that was just used.
    /// </summary>
    public static RefreshToken Issue(
        CompanyId companyId,
        SessionId sessionId,
        RefreshTokenFamilyId familyId,
        RefreshTokenHash tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException("A refresh token must belong to a Company.", nameof(companyId));
        }

        if (sessionId.IsEmpty)
        {
            // An unbound token cannot be revoked per device, which is the whole point of binding
            // it to a session (SD-016).
            throw new ArgumentException(
                "A refresh token must be bound to a session.", nameof(sessionId));
        }

        if (familyId.IsEmpty)
        {
            // Without a family, reuse detection has nothing to revoke.
            throw new ArgumentException(
                "A refresh token must belong to a family.", nameof(familyId));
        }

        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentException(
                "A refresh token must expire after it is issued.", nameof(expiresAtUtc));
        }

        return new RefreshToken(
            RefreshTokenId.New(), companyId, sessionId, familyId, tokenHash, issuedAtUtc, expiresAtUtc);
    }

    /// <summary>Whether the token can still be exchanged.</summary>
    public bool IsRedeemable(DateTimeOffset asAtUtc) =>
        !IsUsed && !IsRevoked && asAtUtc < ExpiresAtUtc;

    /// <summary>
    /// Marks the token consumed and records what replaced it.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if it was already used — the caller must treat that as
    /// reuse and revoke the family, not retry. Doing the check here means the decision cannot be
    /// made by reading <see cref="UsedAtUtc"/> and acting on a stale value.
    /// </remarks>
    public bool TryConsume(RefreshTokenId supersededBy, DateTimeOffset usedAtUtc)
    {
        if (IsUsed)
        {
            return false;
        }

        UsedAtUtc = usedAtUtc;
        SupersededById = supersededBy;
        return true;
    }

    /// <summary>
    /// Revokes the token.
    /// </summary>
    /// <remarks>
    /// Idempotent and independent of use: a token can be both used and revoked, and family
    /// revocation sets this on every member including the ones already consumed.
    /// </remarks>
    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        RevokedAtUtc ??= revokedAtUtc;
    }
}
