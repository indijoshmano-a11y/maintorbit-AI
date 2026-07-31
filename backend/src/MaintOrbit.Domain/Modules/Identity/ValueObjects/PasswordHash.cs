using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// A stored password hash — C4 data.
/// </summary>
/// <remarks>
/// <c>employee_credentials</c> is classified C4: <b>never logged, never in error messages, never
/// leaves production</b> (database-design §4.2). LG-3 states how that is achieved — "absent by
/// construction, not masked after the fact" — and this type is the construction. It cannot be
/// formatted into a message, because the two ways a value normally reaches a log,
/// <see cref="ToString"/> and the debugger display, are both overridden to reveal nothing.
/// <para>
/// A <c>string</c> would satisfy the same schema and would be one interpolation away from a log
/// entry containing a hash. That is not a hypothetical: the usual way a secret reaches a log is
/// an exception message built from whatever was in scope.
/// </para>
/// <para>
/// This type carries the hash only. It performs no hashing and no verification — both are
/// deferred, and both belong outside the domain, where the algorithm's cost parameters and its
/// constant-time comparison live.
/// </para>
/// </remarks>
[DebuggerDisplay("PasswordHash [REDACTED]")]
public sealed record PasswordHash
{
    /// <summary>
    /// Longest hash accepted.
    /// </summary>
    /// <remarks>
    /// No length is stated in the database design. An Argon2id PHC-format string — algorithm,
    /// version, parameters, salt, and digest — runs to roughly 100 characters at ordinary
    /// parameters; 256 leaves room for a parameter review to raise them without a schema change,
    /// while still bounding a column that must never grow unboundedly.
    /// </remarks>
    public const int MaxLength = 256;

    private PasswordHash(string value) => Value = value;

    /// <summary>
    /// The encoded hash.
    /// </summary>
    /// <remarks>
    /// Reaching for this is the point at which C4 data enters ordinary code. The only legitimate
    /// readers are the persistence mapping and the verifier.
    /// </remarks>
    public string Value { get; }

    /// <summary>
    /// Creates a hash from a value produced by a key derivation function.
    /// </summary>
    /// <remarks>
    /// Validation is structural only — that something is present and bounded. Whether the value
    /// is a well-formed Argon2id encoding is the hasher's business, and this type deliberately
    /// knows nothing about the algorithm so that changing it does not change the domain.
    /// </remarks>
    public static bool TryCreate(string? candidate, [NotNullWhen(true)] out PasswordHash? hash)
    {
        hash = null;

        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaxLength
            || candidate.Any(char.IsControl))
        {
            return false;
        }

        hash = new PasswordHash(candidate);
        return true;
    }

    /// <summary>
    /// Creates a hash from a value already known to be well formed.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty, oversized, or malformed.</exception>
    public static PasswordHash Create(string candidate)
    {
        if (!TryCreate(candidate, out var hash))
        {
            // The candidate is never echoed. This message reaches logs, and the value is C4 —
            // even a rejected one, since the usual reason for rejection is truncation rather
            // than the value being meaningless.
            throw new ArgumentException("Value is not a usable password hash.", nameof(candidate));
        }

        return hash;
    }

    /// <summary>
    /// Returns a redaction marker, never the hash.
    /// </summary>
    /// <remarks>
    /// Overridden because <c>record</c> generates a <see cref="ToString"/> that prints every
    /// property. Left alone, logging the credential aggregate — or interpolating this into any
    /// message — would write the hash out in full.
    /// </remarks>
    public override string ToString() => "[REDACTED]";

    /// <summary>
    /// Suppresses the compiler-generated member printing that <c>record</c> provides.
    /// </summary>
    /// <remarks>
    /// The generated <see cref="ToString"/> is built from this method rather than from
    /// <see cref="ToString"/> alone, so overriding <see cref="ToString"/> without this would
    /// still leak through any derived record.
    /// </remarks>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "PrintMembers is the record-generated member the compiler calls on an " +
                        "instance; a static one would not be used and the hash would print.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("[REDACTED]");
        return true;
    }
}
