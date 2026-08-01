using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Generates refresh tokens and hashes presented ones.
/// </summary>
/// <remarks>
/// A port so the application layer can issue and look up tokens without knowing the entropy
/// source, the encoding, or the digest. That the hash is SHA-256 is a decision
/// 09-encryption-strategy §3 makes on entropy grounds, and it belongs behind this seam rather than
/// at every call site.
/// </remarks>
public interface IRefreshTokenFactory
{
    /// <summary>
    /// Creates a new token and its hash.
    /// </summary>
    /// <remarks>
    /// The plaintext is returned exactly once, for delivery to the client. Nothing stores it —
    /// SD-014 requires refresh tokens to be "never recoverable" from the database.
    /// </remarks>
    IssuedRefreshToken Issue();

    /// <summary>Hashes a token presented by a client, so it can be looked up.</summary>
    RefreshTokenHash Hash(string presentedToken);
}

/// <summary>A freshly generated token and the hash that will be stored for it.</summary>
/// <remarks>
/// The two travel together exactly once. <see cref="Token"/> goes to the client and is then
/// unrecoverable; <see cref="Hash"/> is what persists.
/// </remarks>
public sealed record IssuedRefreshToken(string Token, RefreshTokenHash Hash)
{
    /// <inheritdoc />
    public override string ToString() => "[REDACTED]";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
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
