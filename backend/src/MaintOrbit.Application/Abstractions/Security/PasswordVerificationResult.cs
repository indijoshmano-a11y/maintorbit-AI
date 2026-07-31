namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// The outcome of checking a password against a stored hash.
/// </summary>
/// <remarks>
/// Three outcomes rather than a <see cref="bool"/>, because the caller must be able to
/// distinguish "this credential is unusable" from "this password is wrong" — the first is an
/// operational fault worth an alert, the second is an ordinary failed attempt.
/// <para>
/// <b>That distinction must never reach the caller of the API.</b> An authentication response
/// says only that authentication failed; revealing that a stored hash was unreadable tells an
/// attacker the account exists and that something is wrong with it.
/// </para>
/// </remarks>
public enum PasswordVerificationResult
{
    /// <summary>The password does not match.</summary>
    Failed = 0,

    /// <summary>The password matches.</summary>
    Success = 1,

    /// <summary>
    /// The stored hash could not be read.
    /// </summary>
    /// <remarks>
    /// Truncation, a parameter set that no longer parses, or an algorithm this build does not
    /// implement. Treated as a failure to authenticate — never as a pass — but it is a fault in
    /// stored data rather than a wrong guess, and the two want different responses from the
    /// operator.
    /// </remarks>
    Unusable = 2
}
