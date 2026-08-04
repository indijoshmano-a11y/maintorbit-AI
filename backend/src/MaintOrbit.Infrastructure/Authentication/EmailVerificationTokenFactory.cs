using System.Security.Cryptography;
using System.Text;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Generates email verification tokens and hashes presented ones with SHA-256.
/// </summary>
/// <remarks>
/// 09-encryption-strategy §3.1 requires a cryptographically secure RNG for every security value;
/// <see cref="RandomNumberGenerator"/> is that, and nothing here reaches for
/// <see cref="Random"/>, a timestamp, or an identifier — a guessable verification token lets
/// somebody prove control of an address they have never seen.
/// <para>
/// SHA-256 rather than Argon2id, per §3's decision tree: the secret is high-entropy and generated
/// by us, so brute force is infeasible regardless and a slow hash would add cost without adding
/// security. The digest is unsalted, which is correct for this input and would not be for a
/// password — a salt defends against precomputation across a corpus of guessable values, and there
/// is no corpus of 256-bit random tokens to precompute. It is also what allows lookup by hash; a
/// per-row salt would make finding a presented token a table scan.
/// </para>
/// </remarks>
internal sealed class EmailVerificationTokenFactory(IOptions<EmailVerificationOptions> options)
    : IEmailVerificationTokenFactory
{
    /// <inheritdoc />
    public IssuedEmailVerificationToken Issue()
    {
        var bytes = RandomNumberGenerator.GetBytes(options.Value.TokenBytes);

        try
        {
            // URL-safe, because this one travels in a link. Base64 with '+' and '/' would be
            // re-encoded somewhere between the mail client and the browser, and a re-encoding bug
            // turns two distinct tokens into one.
            var token = Base64UrlEncode(bytes);

            return new IssuedEmailVerificationToken(token, Hash(token));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public EmailVerificationTokenHash Hash(string presentedToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedToken);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken));

        return EmailVerificationTokenHash.Create(Convert.ToHexStringLower(digest));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
