using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// An Employee's email address, normalized.
/// </summary>
/// <remarks>
/// A value object rather than a <c>string</c> because the uniqueness rule depends on
/// normalization. <c>ux_employees_company_id_email</c> makes the address unique per Company, and
/// PostgreSQL text comparison is case-sensitive — so <c>Ada@example.com</c> and
/// <c>ada@example.com</c> would both be accepted as distinct Employees of the same Company.
/// Normalizing at construction means there is no path that stores an unnormalized address.
/// <para>
/// Validation here is structural only: it establishes that a value can be stored and compared,
/// not that it can receive mail. Deliverability is settled by the verification flow that sets
/// <c>email_verified_at_utc</c>, which is a later milestone.
/// </para>
/// </remarks>
public sealed record Email
{
    /// <summary>
    /// Longest address accepted.
    /// </summary>
    /// <remarks>
    /// RFC 5321 §4.5.3.1.3 bounds a forward path at 254 characters. No length is stated in the
    /// database design, and a column needs one — this is the standard's limit rather than a
    /// number chosen here.
    /// </remarks>
    public const int MaxLength = 254;

    private Email(string value) => Value = value;

    /// <summary>The normalized address.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates an address from caller-supplied input.
    /// </summary>
    /// <remarks>
    /// An invalid address from a caller is an expected failure, not an exceptional one (EX-1), so
    /// the failure path returns rather than throws. A <c>Result</c> type would be the fuller
    /// answer; none exists yet, and introducing one is a decision wider than this aggregate.
    /// </remarks>
    public static bool TryCreate(string? candidate, [NotNullWhen(true)] out Email? email)
    {
        email = null;

        if (candidate is null)
        {
            return false;
        }

        var normalized = Normalize(candidate);

        if (!IsWellFormed(normalized))
        {
            return false;
        }

        email = new Email(normalized);
        return true;
    }

    /// <summary>
    /// Creates an address from a value already known to be valid.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not well formed.</exception>
    /// <remarks>
    /// For values coming from inside the system — a test, or a row already stored. Reaching this
    /// with caller input means validation was skipped somewhere, which is exceptional and should
    /// be loud.
    /// </remarks>
    public static Email Create(string candidate)
    {
        if (!TryCreate(candidate, out var email))
        {
            // The address is not echoed. It is personal data, and this message reaches logs.
            throw new ArgumentException("Value is not a well-formed email address.", nameof(candidate));
        }

        return email;
    }

    public override string ToString() => Value;

    /// <summary>
    /// Trims surrounding whitespace and lowercases.
    /// </summary>
    /// <remarks>
    /// Invariant lowercasing, not the current culture: under a Turkish culture <c>ToLower</c>
    /// maps <c>I</c> to a dotless <c>ı</c>, so the same address would normalize differently
    /// depending on where the server runs and the uniqueness index would stop holding.
    /// </remarks>
    private static string Normalize(string candidate) =>
        candidate.Trim().ToLowerInvariant();

    private static bool IsWellFormed(string value)
    {
        if (value.Length is 0 or > MaxLength)
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);

        // Exactly one '@', with something on each side.
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            return false;
        }

        var domain = value[(at + 1)..];

        // A domain needs a dot, and cannot start or end with one. This rejects the mistakes that
        // reach a signup form; it does not attempt to be a parser for RFC 5322, which accepts
        // forms no mail system in use will deliver to.
        if (!domain.Contains('.', StringComparison.Ordinal)
            || domain.StartsWith('.')
            || domain.EndsWith('.')
            || domain.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    }
}
