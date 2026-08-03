using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A single-use, time-limited password reset request — C4 data.
/// </summary>
/// <remarks>
/// FR-AUTH-012: "Employees must be able to reset a forgotten password through a verified email
/// flow with a single-use, time-limited token." Those three properties are the aggregate —
/// <see cref="ExpiresAtUtc"/> makes it time-limited, <see cref="ConsumedAtUtc"/> makes it
/// single-use, and <see cref="TokenHash"/> is the only form the secret takes at rest.
/// <para>
/// <b>The request and the token are one row, not two.</b> The documented precedent is
/// <c>invitations</c> (§4.1), which carries <c>token_hash</c>, <c>expires_at_utc</c>, and
/// <c>accepted_at_utc</c> on the record of the invitation itself. A reset is the same shape: the
/// request is what produced the token, and separating them would create a row that can be issued
/// without a secret and a secret with no record of who asked for it.
/// </para>
/// <para>
/// <b>Consumed rows are not deleted.</b> Replaying a reset link must be refused, and a deleted row
/// makes a replay indistinguishable from a typo. The distinction is the same one
/// <see cref="RefreshToken"/> draws for SD-014, and for the same reason: only a retained record
/// can tell them apart.
/// </para>
/// </remarks>
public sealed class PasswordResetToken
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so a token can only come from <see cref="Issue"/>. EF materializes through it,
    /// which correctly bypasses the invariants — a stored row satisfied them when it was written.
    /// </remarks>
    private PasswordResetToken() => TokenHash = null!;

    private PasswordResetToken(
        PasswordResetTokenId id,
        CompanyId companyId,
        EmployeeId employeeId,
        PasswordResetTokenHash tokenHash,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        TokenHash = tokenHash;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Identifier of this request.</summary>
    public PasswordResetTokenId Id { get; private init; }

    /// <summary>
    /// The Company this request belongs to — the tenant discriminator (DB-P1).
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than reached through the Employee, so the row-level security
    /// policy compares against a local column instead of joining to <c>employees</c> per row.
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee whose password this resets.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public PasswordResetTokenHash TokenHash { get; private init; }

    /// <summary>When the reset was requested (§1.7).</summary>
    public DateTimeOffset RequestedAtUtc { get; private init; }

    /// <summary>
    /// The address the request came from, when one was observed.
    /// </summary>
    /// <remarks>
    /// Server-observed, never taken from the request body — the same rule sign-in follows for
    /// <c>sessions.ip_address</c>. Nullable because a request through a proxy chain that strips
    /// the address is still a valid request. Personal data, like the session column §4.2 flags.
    /// </remarks>
    public string? RequestedFromIpAddress { get; private set; }

    /// <summary>When the token stops being accepted.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private init; }

    /// <summary>
    /// When the token was redeemed, or <see langword="null"/> if it has not been.
    /// </summary>
    /// <remarks>
    /// The single-use half of FR-AUTH-012. A token presented with this already set is being
    /// replayed, which is a different observation from an unknown token and must be refused
    /// rather than retried.
    /// </remarks>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>
    /// When the token was invalidated without being used, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Set when a newer request supersedes this one and when a completed reset clears whatever
    /// else was outstanding. Without it an Employee could accumulate live reset links — every
    /// one of them a standing takeover credential for as long as it had left to run.
    /// </remarks>
    public DateTimeOffset? InvalidatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the token has already been redeemed.</summary>
    public bool IsConsumed => ConsumedAtUtc is not null;

    /// <summary>Whether the token was invalidated before it could be redeemed.</summary>
    public bool IsInvalidated => InvalidatedAtUtc is not null;

    /// <summary>
    /// Issues a reset token for an Employee.
    /// </summary>
    /// <remarks>
    /// Takes an already-computed hash. Nothing here hashes, and nothing here accepts the token
    /// itself — a domain type that could hold one is a domain type that can log one.
    /// </remarks>
    /// <exception cref="ArgumentException">The Company or Employee identifier is unset, or the
    /// token would not expire after it was issued.</exception>
    public static PasswordResetToken Issue(
        CompanyId companyId,
        EmployeeId employeeId,
        PasswordResetTokenHash tokenHash,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        string? requestedFromIpAddress = null)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException(
                "A password reset must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            // A reset bound to nobody resets nothing, but it would still be a live recovery
            // credential sitting in the identity schema.
            throw new ArgumentException(
                "A password reset must belong to an Employee.", nameof(employeeId));
        }

        if (expiresAtUtc <= requestedAtUtc)
        {
            // FR-AUTH-012 requires the token to be time-limited. A window that closes before it
            // opens is not a short window, it is a broken one — and it would fail as "expired",
            // which reads as ordinary rather than as a defect.
            throw new ArgumentException(
                "A password reset must expire after it is requested.", nameof(expiresAtUtc));
        }

        return new PasswordResetToken(
            PasswordResetTokenId.New(), companyId, employeeId, tokenHash, requestedAtUtc, expiresAtUtc)
        {
            RequestedFromIpAddress = requestedFromIpAddress
        };
    }

    /// <summary>Whether the token can still be redeemed.</summary>
    public bool IsRedeemable(DateTimeOffset asAtUtc) =>
        !IsConsumed && !IsInvalidated && asAtUtc < ExpiresAtUtc;

    /// <summary>
    /// Marks the token redeemed.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if it was already consumed — the caller must treat that as
    /// a replay and refuse, not retry. Deciding here rather than by reading
    /// <see cref="ConsumedAtUtc"/> means the decision cannot be made against a value that has
    /// since changed; the unique index on the hash and the row version close the rest of the race.
    /// </remarks>
    public bool TryConsume(DateTimeOffset consumedAtUtc)
    {
        if (IsConsumed || IsInvalidated)
        {
            return false;
        }

        ConsumedAtUtc = consumedAtUtc;
        return true;
    }

    /// <summary>
    /// Invalidates an unredeemed token.
    /// </summary>
    /// <remarks>
    /// Idempotent, and deliberately silent about tokens already consumed: a redeemed token is
    /// spent, and stamping it as invalidated as well would lose the distinction between a link
    /// somebody used and one that was superseded.
    /// </remarks>
    public void Invalidate(DateTimeOffset invalidatedAtUtc)
    {
        if (IsConsumed)
        {
            return;
        }

        InvalidatedAtUtc ??= invalidatedAtUtc;
    }
}
