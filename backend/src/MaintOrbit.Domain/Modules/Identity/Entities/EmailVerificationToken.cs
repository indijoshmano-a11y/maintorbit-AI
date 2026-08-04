using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A single-use, time-limited proof that an Employee controls an address — C4 data.
/// </summary>
/// <remarks>
/// FR-AUTH-013: "Email addresses must be verified before an account becomes active."
/// third-party-services §7 restates the same dependency from the other side — "email verification
/// gates account activation".
/// <para>
/// <b>It records the address it was issued for, not just the Employee.</b> That is what makes it a
/// verification rather than a formality: an Employee whose address changes between issuance and
/// redemption must not have the new one verified by a link sent to the old. Nothing changes an
/// address yet, and the check costs one comparison — building it later would mean auditing every
/// token issued before it existed.
/// </para>
/// <para>
/// <b>Consumed rows are not deleted.</b> Redeeming a link twice must be refused, and a deleted row
/// makes a replay indistinguishable from a typo — the same distinction
/// <see cref="PasswordResetToken"/> keeps, for the same reason.
/// </para>
/// </remarks>
public sealed class EmailVerificationToken
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so a token can only come from <see cref="Issue"/>. EF materializes through it,
    /// which correctly bypasses the invariants — a stored row satisfied them when it was written.
    /// </remarks>
    private EmailVerificationToken()
    {
        TokenHash = null!;
        Email = null!;
    }

    private EmailVerificationToken(
        EmailVerificationTokenId id,
        CompanyId companyId,
        EmployeeId employeeId,
        Email email,
        EmailVerificationTokenHash tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        Email = email;
        TokenHash = tokenHash;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Identifier of this request.</summary>
    public EmailVerificationTokenId Id { get; private init; }

    /// <summary>
    /// The Company this request belongs to — the tenant discriminator (DB-P1).
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than reached through the Employee, so the row-level security
    /// policy compares against a local column instead of joining to <c>employees</c> per row.
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee whose address this proves.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>
    /// The address the token was issued for.
    /// </summary>
    /// <remarks>
    /// Checked at redemption against the Employee's current address. A token that verified whatever
    /// address happened to be on the record when it was redeemed would let a changed address
    /// inherit proof it never earned.
    /// </remarks>
    public Email Email { get; private init; }

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public EmailVerificationTokenHash TokenHash { get; private init; }

    /// <summary>When the token was issued (§1.7).</summary>
    public DateTimeOffset IssuedAtUtc { get; private init; }

    /// <summary>When the token stops being accepted.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private init; }

    /// <summary>
    /// When the token was redeemed, or <see langword="null"/> if it has not been.
    /// </summary>
    /// <remarks>
    /// The single-use half. A token presented with this already set is being replayed, which is a
    /// different observation from an unknown token and must be refused rather than retried.
    /// </remarks>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>
    /// When the token was invalidated without being used, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Set when a newer request supersedes this one. Without it an Employee could accumulate live
    /// verification links, each one a standing proof of an address they may since have lost.
    /// </remarks>
    public DateTimeOffset? InvalidatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the token has already been redeemed.</summary>
    public bool IsConsumed => ConsumedAtUtc is not null;

    /// <summary>Whether the token was invalidated before it could be redeemed.</summary>
    public bool IsInvalidated => InvalidatedAtUtc is not null;

    /// <summary>
    /// Issues a verification token for an Employee's address.
    /// </summary>
    /// <remarks>
    /// Takes an already-computed hash. Nothing here hashes, and nothing here accepts the token
    /// itself — a domain type that could hold one is a domain type that can log one.
    /// </remarks>
    /// <exception cref="ArgumentException">The Company or Employee identifier is unset, or the
    /// token would not expire after it was issued.</exception>
    public static EmailVerificationToken Issue(
        CompanyId companyId,
        EmployeeId employeeId,
        Email email,
        EmailVerificationTokenHash tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException(
                "A verification must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            // A verification bound to nobody proves nothing, but it would still be a live
            // credential sitting in the identity schema.
            throw new ArgumentException(
                "A verification must belong to an Employee.", nameof(employeeId));
        }

        if (expiresAtUtc <= issuedAtUtc)
        {
            // FR-AUTH-013's proof has to be time-limited for the same reason FR-AUTH-012's is: a
            // link that never lapses is a permanent credential in somebody's mailbox. A window
            // that closes before it opens is not a short window, it is a broken one — and it would
            // surface as "expired", which reads as ordinary rather than as the defect it is.
            throw new ArgumentException(
                "A verification must expire after it is issued.", nameof(expiresAtUtc));
        }

        return new EmailVerificationToken(
            EmailVerificationTokenId.New(),
            companyId,
            employeeId,
            email,
            tokenHash,
            issuedAtUtc,
            expiresAtUtc);
    }

    /// <summary>Whether the token can still be redeemed.</summary>
    public bool IsRedeemable(DateTimeOffset asAtUtc) =>
        !IsConsumed && !IsInvalidated && asAtUtc < ExpiresAtUtc;

    /// <summary>
    /// Whether this token was issued for the address it is being redeemed against.
    /// </summary>
    /// <remarks>
    /// The check that makes a verification mean something. Comparison is by value object, so it
    /// carries <see cref="Email"/>'s normalization rather than repeating it here.
    /// </remarks>
    public bool Matches(Email address) => Email == address;

    /// <summary>
    /// Marks the token redeemed.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if it was already consumed or superseded — the caller must
    /// treat that as a replay and refuse, not retry. Deciding here rather than by reading
    /// <see cref="ConsumedAtUtc"/> means the decision cannot be made against a value that has since
    /// changed; the unique index on the hash and the row version close the rest of the race.
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
