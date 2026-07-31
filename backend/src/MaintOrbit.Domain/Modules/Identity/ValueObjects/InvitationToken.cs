using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// The single-use token from an invitation email.
/// </summary>
/// <remarks>
/// database-design §4.1 states the properties: the token is <b>hashed</b> at rest, single-use,
/// and time-limited. It is a bearer credential — whoever holds it can complete the invitation —
/// so it is treated as secret material and never printed.
/// <para>
/// <b>This type validates shape, not authenticity.</b> The authoritative check is a lookup of
/// <c>token_hash</c> in <c>tenancy.invitations</c> together with <c>expires_at_utc</c> and
/// <c>accepted_at_utc</c>. That table belongs to the tenancy module, which does not exist yet, so
/// a well-formed token here means "could be a token", never "is a valid, unexpired, unused
/// invitation". The distinction is recorded rather than papered over.
/// </para>
/// </remarks>
[DebuggerDisplay("InvitationToken [REDACTED]")]
public sealed record InvitationToken
{
    /// <summary>
    /// Shortest token accepted.
    /// </summary>
    /// <remarks>
    /// No length is documented. 32 characters is the shortest that can carry 128 bits of
    /// entropy in a URL-safe encoding — below that, a token becomes guessable within the window
    /// it stays valid, which is the one property a time-limited bearer credential depends on.
    /// </remarks>
    public const int MinLength = 32;

    /// <summary>Longest token accepted.</summary>
    public const int MaxLength = 256;

    private InvitationToken(string value) => Value = value;

    /// <summary>The raw token as presented by the caller.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a token from caller input.
    /// </summary>
    /// <remarks>
    /// Restricted to the URL-safe base64 alphabet. A token arrives from a link, so anything
    /// outside that alphabet did not come from one — and rejecting early keeps unexpected input
    /// away from the lookup that will eventually consume it.
    /// </remarks>
    public static bool TryCreate(string? candidate, [NotNullWhen(true)] out InvitationToken? token)
    {
        token = null;

        if (candidate is null || candidate.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        token = new InvitationToken(candidate);
        return true;
    }

    /// <summary>
    /// Creates a token from a value already known to be well formed.
    /// </summary>
    /// <exception cref="ArgumentException">The token is malformed.</exception>
    public static InvitationToken Create(string candidate)
    {
        if (!TryCreate(candidate, out var token))
        {
            // The candidate is not echoed: it is a bearer credential, and this message reaches
            // logs (LG-2).
            throw new ArgumentException("Value is not a well-formed invitation token.", nameof(candidate));
        }

        return token;
    }

    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";

    /// <summary>Suppresses the member printing a record generates.</summary>
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
