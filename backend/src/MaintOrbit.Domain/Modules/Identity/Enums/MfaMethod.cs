namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// Which second factor an enrolment represents.
/// </summary>
/// <remarks>
/// One member, and the enum exists anyway. FR-AUTH-020 adds hardware security keys at v2.0, and
/// 02-authentication-architecture §3.6 says plainly that they "do not fit the shared-secret model
/// and require a distinct credential type" — so the column that distinguishes them has to be there
/// before the second type arrives, or the first migration of that milestone is a backfill.
/// <para>
/// Stored as text, not an ordinal: a renumbered enum silently reinterprets every stored row.
/// </para>
/// </remarks>
public enum MfaMethod
{
    /// <summary>Time-based one-time password, RFC 6238 (FR-AUTH-005).</summary>
    Totp = 0
}
