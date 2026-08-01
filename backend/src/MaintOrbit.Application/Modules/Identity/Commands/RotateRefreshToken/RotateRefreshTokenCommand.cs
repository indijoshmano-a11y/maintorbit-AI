using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Security;

namespace MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;

/// <summary>Exchanges a refresh token for a new access token and a replacement refresh token.</summary>
/// <remarks>
/// Carries a bearer credential, so neither <see cref="ToString"/> nor the record's generated member
/// printing reveals it.
/// </remarks>
[DebuggerDisplay("RotateRefreshTokenCommand [REDACTED]")]
public sealed record RotateRefreshTokenCommand(string PresentedToken)
    : ICommand<RefreshedTokens>
{
    /// <inheritdoc />
    public override string ToString() => "RotateRefreshTokenCommand { [REDACTED] }";

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the token would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}

/// <summary>The pair returned by a successful authentication or rotation.</summary>
/// <remarks>
/// The refresh token is plaintext and exists only here — it is hashed before storage and is
/// unrecoverable afterwards (SD-014).
/// </remarks>
public sealed record RefreshedTokens(AccessToken AccessToken, string RefreshToken)
{
    /// <inheritdoc />
    public override string ToString() => "RefreshedTokens { [REDACTED] }";

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the token would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}
