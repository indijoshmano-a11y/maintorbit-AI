namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Generates TOTP secrets and checks codes against them (RFC 6238).
/// </summary>
/// <remarks>
/// A port so handlers can verify a second factor without knowing the digest, the digit count, or
/// the step length. 02-authentication-architecture §3.6 fixes the method as "TOTP,
/// standards-based" and nothing more, so the parameters are the standard's defaults and live
/// behind this seam rather than at every call site.
/// </remarks>
public interface ITotpService
{
    /// <summary>
    /// Generates a new shared secret.
    /// </summary>
    /// <remarks>
    /// From a cryptographically secure RNG, which 09-encryption-strategy §3.1 requires for every
    /// security value and §3.8 repeats for tokens and identifiers. Returned as bytes, so the
    /// caller seals it without a string of it ever existing.
    /// </remarks>
    byte[] GenerateSecret();

    /// <summary>Formats a secret the way an authenticator app expects to receive it.</summary>
    string Encode(ReadOnlySpan<byte> secret);

    /// <summary>The time step a given instant falls in.</summary>
    /// <remarks>
    /// Exposed because the step, not the code, is what gets spent — replay protection records a
    /// step and refuses anything not later, so the caller needs the same notion of "which window".
    /// </remarks>
    long TimeStepAt(DateTimeOffset instant);

    /// <summary>
    /// Whether a presented code is the one this secret produces for this instant.
    /// </summary>
    /// <remarks>
    /// <b>No tolerance window.</b> Only the step containing <paramref name="asAt"/> is checked.
    /// RFC 6238 §5.2 permits accepting adjacent steps for clock drift, and none of the platform
    /// documentation specifies one — so a window would be a number chosen here, and each extra
    /// step accepted is another window in which an observed code still works.
    /// </remarks>
    bool IsValid(ReadOnlySpan<byte> secret, string presentedCode, DateTimeOffset asAt);
}
