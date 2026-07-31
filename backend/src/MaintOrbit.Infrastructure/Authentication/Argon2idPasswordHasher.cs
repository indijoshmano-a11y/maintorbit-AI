using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Argon2id password hashing (SD-010).
/// </summary>
/// <remarks>
/// Argon2id is memory-hard, which is the property that matters: it makes each offline guess
/// expensive in RAM as well as CPU, so an attacker with a leaked table cannot trade memory for
/// massive GPU parallelism the way they can against a fast hash
/// (security-architecture §691).
/// <para>
/// The algorithm is not negotiable at runtime. A hasher that accepted several algorithms would
/// verify whatever a stored row claimed, which turns a row-level write into an algorithm
/// downgrade. This one derives with Argon2id and reads only <c>$argon2id$</c>; anything else is
/// reported unusable.
/// </para>
/// </remarks>
internal sealed class Argon2idPasswordHasher(IOptions<PasswordHashingOptions> options)
    : IPasswordHasher
{
    private PasswordHashingOptions Options => options.Value;

    /// <inheritdoc />
    public PasswordHashVersion CurrentVersion => new(Options.Version);

    /// <inheritdoc />
    public string CurrentParameters
    {
        get
        {
            var current = Options;

            // The same field the PHC string carries, in the same form, so a row and its hash
            // never disagree about what produced it.
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"m={current.MemoryKibibytes},t={current.Iterations},p={current.Parallelism}");
        }
    }

    /// <inheritdoc />
    public PasswordHash Hash(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            // A programming error, not a user one: whatever calls this has already applied the
            // strength policy (FR-AUTH-002). Thrown rather than hashed, because an empty password
            // stored as a credential is an account anyone can open. The message carries no input.
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        var current = Options;
        var salt = RandomNumberGenerator.GetBytes(current.SaltLengthBytes);

        var derived = Derive(
            password,
            salt,
            current.MemoryKibibytes,
            current.Iterations,
            current.Parallelism,
            current.HashLengthBytes);

        try
        {
            return PasswordHash.Create(PhcString.Encode(
                current.MemoryKibibytes,
                current.Iterations,
                current.Parallelism,
                salt,
                derived));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <inheritdoc />
    public PasswordVerificationResult Verify(PasswordHash hash, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (password.IsEmpty)
        {
            // Caller input, so an expected failure rather than an exceptional one (EX-1). No hash
            // this class produces can have come from an empty password, so it cannot match — and
            // deriving anyway would raise from the key derivation function, turning a routine
            // failed attempt into an exception on the authentication path.
            return PasswordVerificationResult.Failed;
        }

        if (!PhcString.TryDecode(hash.Value, out var stored))
        {
            // Truncated, non-conforming, or produced by another algorithm. Reported, never
            // thrown — the value being parsed is C4, and an exception carries it outward.
            return PasswordVerificationResult.Unusable;
        }

        if (stored.Version != PhcString.Argon2Version)
        {
            return PasswordVerificationResult.Unusable;
        }

        // Re-derived with the stored parameters, not the configured ones. This is what §4.2 means
        // by a parameter change not invalidating existing hashes: a credential written last year
        // still verifies against last year's costs.
        var candidate = Derive(
            password,
            stored.Salt,
            stored.MemoryKibibytes,
            stored.Iterations,
            stored.Parallelism,
            stored.Hash.Length);

        try
        {
            // Fixed-time comparison. A byte-by-byte equality check returns sooner for a wrong
            // guess that shares a longer prefix, and that difference is measurable across enough
            // attempts — which is how an attacker recovers a hash one byte at a time.
            return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash)
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(stored.Hash);
        }
    }

    /// <inheritdoc />
    public bool NeedsRehash(PasswordHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (!PhcString.TryDecode(hash.Value, out var stored))
        {
            // Unreadable, so it cannot be shown to meet the current parameters. Saying "yes"
            // means the next successful authentication replaces it, which is the outcome wanted.
            return true;
        }

        var current = Options;

        // Weaker on any axis, not all of them. Each parameter contributes independently, so a
        // credential matching the current memory cost but half the iterations is still below the
        // reviewed standard.
        return stored.Version != PhcString.Argon2Version
               || stored.MemoryKibibytes < current.MemoryKibibytes
               || stored.Iterations < current.Iterations
               || stored.Parallelism < current.Parallelism
               || stored.Hash.Length < current.HashLengthBytes
               || stored.Salt.Length < current.SaltLengthBytes;
    }

    /// <summary>
    /// Runs the key derivation function.
    /// </summary>
    /// <remarks>
    /// The plaintext is encoded into a rented-free array that is zeroed in a <c>finally</c>. A
    /// <see cref="string"/> would be immutable and uncollectable on demand, so the password would
    /// persist in the heap until a garbage collection that may never be observed — and would be
    /// readable in any memory dump taken in between.
    /// </remarks>
    private static byte[] Derive(
        ReadOnlySpan<char> password,
        byte[] salt,
        int memoryKibibytes,
        int iterations,
        int parallelism,
        int hashLength)
    {
        var maximumBytes = Encoding.UTF8.GetMaxByteCount(password.Length);
        var buffer = new byte[maximumBytes];

        try
        {
            var written = Encoding.UTF8.GetBytes(password, buffer);

            using var argon2 = new Argon2id(buffer.AsSpan(0, written).ToArray())
            {
                Salt = salt,
                MemorySize = memoryKibibytes,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

            return argon2.GetBytes(hashLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
