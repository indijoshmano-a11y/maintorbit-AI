using MaintOrbit.Application.Abstractions.Messaging;

namespace MaintOrbit.Application.Modules.Identity.Commands.Mfa;

/// <summary>
/// Starts TOTP enrolment for the authenticated Employee (FR-AUTH-005).
/// </summary>
/// <remarks>
/// It carries nothing. The Employee comes from the validated access token, never from the request
/// — a field naming one would let a caller enrol a factor on somebody else's account, which is
/// takeover rather than protection.
/// </remarks>
public sealed record BeginMfaEnrollmentCommand : ICommand<MfaEnrollmentSecret>;

/// <summary>
/// What the Employee needs to add the account to an authenticator app.
/// </summary>
/// <remarks>
/// <b>Returned exactly once, and only to the Employee it belongs to.</b> After confirmation the
/// secret is only ever read as ciphertext; there is no endpoint that shows it again, because a
/// second look is indistinguishable from an attacker's first.
/// <para>
/// <b>No QR image.</b> Nothing in the documentation calls for one, and generating images
/// server-side would put an image encoder on an authenticated path to render a secret. The
/// <c>otpauth://</c> URI is the standard's own Key Uri Format; a client that wants a QR code
/// renders this string locally, where the secret is already.
/// </para>
/// </remarks>
/// <param name="Secret">The shared secret, base32, for manual entry.</param>
/// <param name="Uri">The <c>otpauth://totp/</c> URI carrying the same secret.</param>
public sealed record MfaEnrollmentSecret(string Secret, string Uri)
{
    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the secret would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>
/// Proves possession of the enrolled secret and turns the factor on.
/// </summary>
/// <param name="Code">A code from the authenticator app.</param>
public sealed record ConfirmMfaEnrollmentCommand(string? Code) : ICommand<MfaRecoveryCodes>;

/// <summary>
/// The recovery codes, shown once at confirmation.
/// </summary>
/// <remarks>
/// §3.6 issues them once. Nothing stores the plaintext, so this response is the only time they
/// exist — an Employee who loses them re-enrols rather than asks for them again.
/// </remarks>
public sealed record MfaRecoveryCodes(IReadOnlyList<string> Codes)
{
    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the codes would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>
/// Satisfies a second-factor challenge with a TOTP code or a recovery code.
/// </summary>
/// <remarks>
/// One command for both, because the Employee is answering one question — "prove the second
/// factor" — and splitting it would make the client decide which endpoint to call based on what
/// they typed. The handler tells them apart by shape.
/// </remarks>
/// <param name="Code">A TOTP code, or one of the recovery codes.</param>
public sealed record VerifyMfaChallengeCommand(string? Code) : ICommand<MfaVerification>;

/// <summary>The outcome of a satisfied challenge.</summary>
/// <param name="UsedRecoveryCode">Whether a recovery code was spent rather than a TOTP code.</param>
/// <param name="RemainingRecoveryCodes">How many recovery codes are left unspent.</param>
public sealed record MfaVerification(bool UsedRecoveryCode, int RemainingRecoveryCodes);

/// <summary>
/// Turns the second factor off.
/// </summary>
/// <remarks>
/// Requires a current code. Disabling MFA is exactly what an attacker holding a hijacked session
/// wants to do first, and §3.6's step-up principle — "re-proving possession of the second factor
/// is cheap relative to the consequence" — applies most obviously to removing it.
/// </remarks>
/// <param name="Code">A TOTP code, or one of the recovery codes.</param>
public sealed record DisableMfaCommand(string? Code) : ICommand;
