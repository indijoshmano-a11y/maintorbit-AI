using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Api.Endpoints;

/// <summary>Sign-in request.</summary>
/// <remarks>
/// Carries a plaintext password, so it does not print. A request object is exactly what gets
/// logged when model binding or validation goes wrong.
/// <para>
/// There is no Company field. TC-1 derives the tenant server-side from the credential; a Company
/// the caller could name would be a caller choosing which tenant to authenticate against.
/// </para>
/// </remarks>
[DebuggerDisplay("SignInRequest [REDACTED]")]
public sealed record SignInRequest
{
    /// <summary>The Employee's email address.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(254, MinimumLength = 3)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The password.</summary>
    /// <remarks>
    /// Bounded only to stop an unbounded body reaching the hasher — Argon2id's cost is paid on
    /// whatever it is given. Strength is a Company-configured policy (FR-AUTH-002) enforced when a
    /// password is set, not when one is presented.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(1024, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Which client surface is signing in. Descriptive only.</summary>
    public string? ClientType { get; init; }

    /// <summary>A human-readable device name for the Employee's own session list.</summary>
    [StringLength(128)]
    public string? DeviceLabel { get; init; }

    /// <inheritdoc />
    public override string ToString() => "SignInRequest { [REDACTED] }";

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the password would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>Refresh request.</summary>
[DebuggerDisplay("RefreshRequest [REDACTED]")]
public sealed record RefreshRequest
{
    /// <summary>The refresh token issued by the previous sign-in or refresh.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(512, MinimumLength = 16)]
    public string RefreshToken { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => "RefreshRequest { [REDACTED] }";

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the token would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>Password reset request (FR-AUTH-012).</summary>
/// <remarks>
/// Only an address. There is no Company field for the same reason sign-in has none, and no
/// callback URL — a caller-supplied redirect is how a reset link gets pointed at somebody else's
/// site with a live token attached.
/// </remarks>
public sealed record PasswordResetRequest
{
    /// <summary>The address to send the reset link to.</summary>
    /// <remarks>
    /// Length-bounded but not otherwise checked here. Whether it is well formed, and whether any
    /// Employee holds it, must not change the response — see the endpoint.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(254, MinimumLength = 3)]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Password reset completion (FR-AUTH-012).</summary>
/// <remarks>
/// Carries both a live reset token and a plaintext password, so it does not print. A request
/// object is exactly what gets logged when model binding or validation goes wrong.
/// </remarks>
[DebuggerDisplay("PasswordResetCompletion [REDACTED]")]
public sealed record PasswordResetCompletion
{
    /// <summary>The token from the emailed link.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(512, MinimumLength = 16)]
    public string Token { get; init; } = string.Empty;

    /// <summary>The password to set.</summary>
    /// <remarks>
    /// Bounded only to stop an unbounded body reaching the hasher — Argon2id's cost is paid on
    /// whatever it is given. Strength is a Company-configured policy (FR-AUTH-002) enforced when a
    /// password is set, and lands with the validation pipeline.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(1024, MinimumLength = 1)]
    public string NewPassword { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => "PasswordResetCompletion { [REDACTED] }";

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and both secrets would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>A presented second factor — a TOTP code or a recovery code (FR-AUTH-005).</summary>
/// <remarks>
/// Carries a live credential, so it does not print. One field for both kinds: the Employee is
/// answering one question, and asking the client to decide which endpoint to call based on what
/// was typed would move that judgement to the least informed place.
/// </remarks>
[DebuggerDisplay("MfaCodeRequest [REDACTED]")]
public sealed record MfaCodeRequest
{
    /// <summary>A six-digit code from the authenticator app, or one of the recovery codes.</summary>
    /// <remarks>
    /// Bounded only to stop an unbounded body reaching the verifier. Which shape it is, and
    /// whether it is right, are the handler's to decide — a length check here that distinguished
    /// the two would report which kind the caller had guessed at.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => "MfaCodeRequest { [REDACTED] }";

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the code would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>What an Employee needs to add the account to an authenticator app.</summary>
/// <remarks>
/// Returned exactly once, to the Employee it belongs to. No QR image: nothing documented calls
/// for one, and a client that wants a QR code renders <see cref="Uri"/> locally, where the secret
/// already is.
/// </remarks>
public sealed record MfaEnrollmentResponse(string Secret, string Uri);

/// <summary>The recovery codes, shown once at confirmation (§3.6).</summary>
public sealed record MfaRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>The outcome of a satisfied challenge.</summary>
/// <remarks>
/// <c>remainingRecoveryCodes</c> is returned so an Employee can see how close they are to having
/// none left. Running out silently is how a lost authenticator becomes a lost account.
/// </remarks>
public sealed record MfaVerificationResponse(bool UsedRecoveryCode, int RemainingRecoveryCodes);

/// <summary>Sign-in response.</summary>
/// <remarks>
/// Serialized camelCase (§1.6). <c>expiresAt</c> is the access token's expiry, so a client can
/// refresh ahead of it rather than discovering it through a failed request; the refresh token's own
/// lifetime is deliberately not disclosed.
/// </remarks>
public sealed record SignInResponse(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string SessionId);

/// <summary>Refresh response.</summary>
/// <remarks>
/// No session identifier: refreshing does not change the session, and repeating it would suggest
/// it might.
/// </remarks>
public sealed record RefreshResponse(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
