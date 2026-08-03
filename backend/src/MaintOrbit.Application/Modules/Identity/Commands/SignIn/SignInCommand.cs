using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Modules.Identity.Commands.SignIn;

/// <summary>Authenticates an Employee and establishes a device session.</summary>
/// <remarks>Carries a plaintext password, so neither it nor its members print.</remarks>
[DebuggerDisplay("SignInCommand [REDACTED]")]
public sealed record SignInCommand(
    string Email,
    string Password,
    SessionClientType ClientType,
    string? DeviceLabel,
    string? IpAddress) : ICommand<SignInResult>
{
    /// <inheritdoc />
    public override string ToString() => "SignInCommand { [REDACTED] }";

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

/// <summary>What a successful sign-in returns.</summary>
/// <remarks>
/// The refresh token is plaintext and exists only here — it is hashed before storage and
/// unrecoverable afterwards (SD-014).
/// </remarks>
public sealed record SignInResult(AccessToken AccessToken, string RefreshToken, SessionId SessionId)
{
    /// <inheritdoc />
    public override string ToString() => "SignInResult { [REDACTED] }";

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the tokens would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("[REDACTED]");
        return true;
    }
}
