namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// Why a session was revoked.
/// </summary>
/// <remarks>
/// The triggers enumerated in 11-session-management §3.5. Stored because
/// <c>revocation_reason</c> is a documented column and because the difference matters after the
/// fact: a session ended by logout and a session ended by detected token reuse look identical
/// without it, and only one of them is an incident.
/// </remarks>
public enum SessionRevocationReason
{
    /// <summary>The Employee logged out of this device session.</summary>
    LoggedOut = 0,

    /// <summary>The Employee terminated this session from their device list (FR-AUTH-008).</summary>
    TerminatedByEmployee = 1,

    /// <summary>A Company Admin terminated the Employee's sessions (FR-AUTH-009).</summary>
    TerminatedByAdministrator = 2,

    /// <summary>The password changed, which ends every session (NFR-SEC-017).</summary>
    PasswordChanged = 3,

    /// <summary>
    /// A refresh token was presented twice (SD-014).
    /// </summary>
    /// <remarks>
    /// The one reason that is a security event rather than an ordinary lifecycle transition. It
    /// means two parties held the same token.
    /// </remarks>
    RefreshTokenReuseDetected = 4,

    /// <summary>The account was locked after repeated failures (FR-AUTH-011).</summary>
    AccountLockedOut = 5,

    /// <summary>The Employee was deprovisioned (FR-AUTH-018).</summary>
    Deprovisioned = 6
}
