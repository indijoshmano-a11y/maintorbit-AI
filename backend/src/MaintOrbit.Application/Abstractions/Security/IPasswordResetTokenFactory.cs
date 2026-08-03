using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Generates password reset tokens and hashes presented ones.
/// </summary>
/// <remarks>
/// A port so the application layer can issue and look up reset tokens without knowing the entropy
/// source, the encoding, or the digest. That the hash is SHA-256 is a decision
/// 09-encryption-strategy §3 makes on entropy grounds, and it belongs behind this seam rather than
/// at every call site.
/// <para>
/// Separate from <see cref="IRefreshTokenFactory"/> despite the identical shape. The two secrets
/// have different lifetimes, different lengths, and different blast radii — a reset token can take
/// over an account outright — and a shared factory would make one setting govern both.
/// </para>
/// </remarks>
public interface IPasswordResetTokenFactory
{
    /// <summary>
    /// Creates a new token and its hash.
    /// </summary>
    /// <remarks>
    /// The plaintext is returned exactly once, for delivery by email. Nothing stores it: a
    /// database holding live reset tokens is a database holding account takeovers.
    /// </remarks>
    IssuedPasswordResetToken Issue();

    /// <summary>Hashes a token presented by a client, so it can be looked up.</summary>
    PasswordResetTokenHash Hash(string presentedToken);
}

/// <summary>A freshly generated reset token and the hash that will be stored for it.</summary>
/// <remarks>
/// The two travel together exactly once. <see cref="Token"/> goes to the Employee's verified
/// address and is then unrecoverable; <see cref="Hash"/> is what persists.
/// </remarks>
public sealed record IssuedPasswordResetToken(string Token, PasswordResetTokenHash Hash)
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
