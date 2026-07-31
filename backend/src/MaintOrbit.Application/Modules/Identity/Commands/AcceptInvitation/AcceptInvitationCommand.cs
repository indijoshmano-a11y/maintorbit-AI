using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;

/// <summary>
/// Completes an invitation: sets the Employee's first password and activates the account.
/// </summary>
/// <remarks>
/// <b>This command carries two secrets</b> — the invitation token, a bearer credential, and the
/// plaintext password. A command is exactly the kind of object that gets logged wholesale when
/// something goes wrong, and a <c>record</c> prints every property by default, so both the
/// generated member printing and <see cref="ToString"/> are suppressed here.
/// <para>
/// The password is a <see cref="string"/> because it arrives as one from a JSON body and cannot
/// be anything else at this boundary. It is passed onward as a span and never copied, so the
/// single instance the request already created is the only one that exists.
/// </para>
/// </remarks>
[DebuggerDisplay("AcceptInvitationCommand [REDACTED]")]
public sealed record AcceptInvitationCommand(
    EmployeeId EmployeeId,
    InvitationToken Token,
    string Password) : ICommand
{
    /// <inheritdoc />
    public override string ToString() => "AcceptInvitationCommand { [REDACTED] }";

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the secrets would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}
