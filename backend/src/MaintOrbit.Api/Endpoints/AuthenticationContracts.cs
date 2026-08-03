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
