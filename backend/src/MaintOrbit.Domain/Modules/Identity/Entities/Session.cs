using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A device-scoped authenticated session (SD-016).
/// </summary>
/// <remarks>
/// The unit is a <b>device session</b>, not an Employee session: someone signed in on a laptop, a
/// desktop, and the VS Code Extension has three, and terminating one leaves the others alone
/// (FR-AUTH-008). That is only possible because the session — not the Employee — is what refresh
/// tokens bind to.
/// <para>
/// <b>Two of the three expiry timers live here.</b> §14 describes three: access token expiry,
/// which is carried in the token and not stored; the idle window, which is
/// <see cref="LastActiveAtUtc"/> plus a Company-configured duration; and
/// <see cref="AbsoluteExpiresAtUtc"/>, stored because it is the one activity cannot defeat.
/// </para>
/// <para>
/// <b>No clock is read here.</b> Every point in time is supplied by the caller (U-3, AT-9), which
/// is also what makes an idle timeout testable without waiting for one.
/// </para>
/// </remarks>
public sealed class Session
{
    /// <summary>Constructor for the persistence layer.</summary>
    private Session()
    {
    }

    private Session(
        SessionId id,
        CompanyId companyId,
        EmployeeId employeeId,
        SessionClientType clientType,
        DateTimeOffset startedAtUtc,
        DateTimeOffset absoluteExpiresAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        ClientType = clientType;
        CreatedAtUtc = startedAtUtc;
        LastActiveAtUtc = startedAtUtc;
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
        UpdatedAtUtc = startedAtUtc;
    }

    /// <summary>Identifier. Appears in the access token's <c>sid</c> claim.</summary>
    public SessionId Id { get; private init; }

    /// <summary>The Company this session belongs to — the tenant discriminator (DB-P1).</summary>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee who authenticated.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>
    /// A human-readable name for the device, if the client supplied one.
    /// </summary>
    /// <remarks>
    /// Shown in the Employee's device list so they can recognise their own sessions. Caller-supplied
    /// and therefore never trusted for anything but display.
    /// </remarks>
    public string? DeviceLabel { get; private set; }

    /// <summary>Which client surface established the session.</summary>
    public SessionClientType ClientType { get; private init; }

    /// <summary>
    /// The address the session was last seen from.
    /// </summary>
    /// <remarks>
    /// Personal data about an Employee even though the table is C2 (§4.2). Retained for a bounded
    /// period and <b>visible to the Employee</b> under principle P-7 — it is collected so they can
    /// recognise a session that is not theirs, so withholding it from them would defeat the reason
    /// for holding it.
    /// </remarks>
    public string? IpAddress { get; private set; }

    /// <summary>Coarse location derived from the address. Personal data, as above.</summary>
    public string? CoarseLocation { get; private set; }

    /// <summary>When the session was established (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>
    /// When the session was last used for genuine activity.
    /// </summary>
    /// <remarks>
    /// The idle window is measured from here. §3.2 is specific that it "resets on genuine user
    /// activity, not on background polling" — a console tab left open must not keep a session alive
    /// indefinitely, which is what makes the caller, not this aggregate, responsible for deciding
    /// what counts as activity.
    /// </remarks>
    public DateTimeOffset LastActiveAtUtc { get; private set; }

    /// <summary>
    /// When the session ends regardless of activity.
    /// </summary>
    /// <remarks>
    /// §3.2: "the one that cannot be defeated by activity". An attacker holding a live session
    /// cannot extend it indefinitely by using it, which is precisely what an idle timeout alone
    /// would allow.
    /// </remarks>
    public DateTimeOffset AbsoluteExpiresAtUtc { get; private init; }

    /// <summary>When the session was revoked, or <see langword="null"/> if it was not.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Why it was revoked.</summary>
    public SessionRevocationReason? RevocationReason { get; private set; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the session has been revoked.</summary>
    public bool IsRevoked => RevokedAtUtc is not null;

    /// <summary>
    /// Establishes a session for an authenticated Employee.
    /// </summary>
    /// <remarks>
    /// The absolute expiry is computed by the caller from Company configuration (FR-AUTH-007) and
    /// passed in, rather than derived here from a duration this aggregate would have to know about.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An identifier is unset, or the absolute expiry is not after the start.
    /// </exception>
    public static Session Start(
        CompanyId companyId,
        EmployeeId employeeId,
        SessionClientType clientType,
        DateTimeOffset startedAtUtc,
        DateTimeOffset absoluteExpiresAtUtc,
        string? deviceLabel = null,
        string? ipAddress = null,
        string? coarseLocation = null)
    {
        if (companyId.IsEmpty)
        {
            throw new ArgumentException("A session must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            throw new ArgumentException("A session must belong to an Employee.", nameof(employeeId));
        }

        if (absoluteExpiresAtUtc <= startedAtUtc)
        {
            // A session that expires at or before it starts is never usable, and would be
            // indistinguishable from one that expired legitimately.
            throw new ArgumentException(
                "A session must expire after it starts.", nameof(absoluteExpiresAtUtc));
        }

        return new Session(
            SessionId.New(), companyId, employeeId, clientType, startedAtUtc, absoluteExpiresAtUtc)
        {
            DeviceLabel = deviceLabel,
            IpAddress = ipAddress,
            CoarseLocation = coarseLocation
        };
    }

    /// <summary>
    /// Whether the session may still be used.
    /// </summary>
    /// <remarks>
    /// Checks all three conditions §14 defines for a stored session: not revoked, within the
    /// absolute lifetime, and within the idle window. The idle window is a parameter because it is
    /// Company-configured (FR-AUTH-007) and this aggregate holds no Company settings.
    /// </remarks>
    public bool IsActive(DateTimeOffset asAtUtc, TimeSpan idleTimeout) =>
        !IsRevoked
        && asAtUtc < AbsoluteExpiresAtUtc
        && asAtUtc < LastActiveAtUtc + idleTimeout;

    /// <summary>
    /// Records genuine activity, resetting the idle window.
    /// </summary>
    /// <remarks>
    /// Refuses on a revoked or expired session rather than silently reviving one — a resurrected
    /// session is the failure that makes revocation meaningless.
    /// </remarks>
    public Result RecordActivity(DateTimeOffset asAtUtc, TimeSpan idleTimeout)
    {
        if (!IsActive(asAtUtc, idleTimeout))
        {
            return Result.Failure(Error.AuthenticationFailed("The session is no longer active."));
        }

        // Monotonic. A clock adjustment or an out-of-order request must not move activity
        // backwards and shorten the window.
        if (asAtUtc > LastActiveAtUtc)
        {
            LastActiveAtUtc = asAtUtc;
            UpdatedAtUtc = asAtUtc;
        }

        return Result.Success();
    }

    /// <summary>
    /// Revokes the session.
    /// </summary>
    /// <remarks>
    /// Idempotent, and deliberately keeps the <i>first</i> reason. A session revoked by logout and
    /// later swept by a password change was ended by the logout; overwriting would erase the fact
    /// that it had already been closed, which is exactly what an investigation needs.
    /// </remarks>
    public void Revoke(SessionRevocationReason reason, DateTimeOffset revokedAtUtc)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = revokedAtUtc;
        RevocationReason = reason;
        UpdatedAtUtc = revokedAtUtc;
    }
}
