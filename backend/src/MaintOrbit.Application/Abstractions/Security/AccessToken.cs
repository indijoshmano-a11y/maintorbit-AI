using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// An issued access token.
/// </summary>
/// <remarks>
/// A bearer credential: whoever holds it is the Employee for as long as it is valid. It is
/// therefore treated the same way as a password hash — <see cref="ToString"/> and the member
/// printing a <c>record</c> generates are both suppressed, so it cannot reach a log by being
/// interpolated into a message.
/// <para>
/// <see cref="ExpiresAtUtc"/> is returned alongside so a client can refresh before expiry rather
/// than discovering it through a failed request. It is a copy of the <c>exp</c> claim, not a
/// second source of truth — validation reads the claim.
/// </para>
/// </remarks>
[DebuggerDisplay("AccessToken [REDACTED]")]
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc)
{
    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";

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
