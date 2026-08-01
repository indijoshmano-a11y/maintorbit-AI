using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MaintOrbit.Application.Abstractions.Messaging;

namespace MaintOrbit.Application.Modules.Identity.Commands.Login;

/// <summary>
/// Authenticates an Employee with an email address and password (FR-AUTH-001).
/// </summary>
/// <remarks>
/// Carries a plaintext password, and a <c>record</c> prints every property by default — so both
/// <see cref="ToString"/> and the generated member printing are suppressed. A failed login is
/// precisely the moment someone logs the request that caused it.
/// <para>
/// The email is <see cref="string"/> rather than <c>Email</c> because it is unvalidated caller
/// input at this point. Parsing it is the handler's first act, and a malformed address must
/// produce the same answer as a valid one for an account that does not exist.
/// </para>
/// </remarks>
[DebuggerDisplay("LoginCommand [REDACTED]")]
public sealed record LoginCommand(string Email, string Password) : ICommand<AuthenticationResult>
{
    /// <inheritdoc />
    public override string ToString() => "LoginCommand { [REDACTED] }";

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the credentials would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}
