using System.Diagnostics.CodeAnalysis;

namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// The stable identifier of a permission — <c>resource.action</c>.
/// </summary>
/// <remarks>
/// A text code, not a UUID. database-design §1.6 makes reference data one of the two exceptions to
/// UUID primary keys: "human-readable, referenced in configuration, never enumerated by an
/// attacker". A permission is referenced by name in role definitions, in endpoint metadata, and in
/// support conversations, and a UUID would make all three unreadable.
/// <para>
/// The form is <c>resource.action</c> in kebab-case, as §4.2's examples show —
/// <c>provider-connection.create</c>, <c>budget.manage</c>, <c>audit.read</c>. Validating the shape
/// is what stops a typo becoming a permission nobody holds: under deny-by-default (SD-001) a
/// misspelled code is not an error, it is a silent refusal.
/// </para>
/// </remarks>
public readonly record struct PermissionCode
{
    /// <summary>Longest code accepted.</summary>
    public const int MaxLength = 64;

    private PermissionCode(string value) => Value = value;

    /// <summary>The code.</summary>
    public string Value { get; }

    /// <summary>Whether this code was never set.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Creates a code from a candidate, if it is well formed.</summary>
    public static bool TryCreate(string? candidate, out PermissionCode code)
    {
        code = default;

        if (candidate is null || candidate.Length is 0 or > MaxLength)
        {
            return false;
        }

        var separator = candidate.IndexOf('.', StringComparison.Ordinal);

        // Exactly one dot, with a resource and an action either side.
        if (separator <= 0
            || separator != candidate.LastIndexOf('.')
            || separator == candidate.Length - 1)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            var permitted = char.IsAsciiLetterLower(character)
                            || char.IsAsciiDigit(character)
                            || character is '-' or '.';

            if (!permitted)
            {
                return false;
            }
        }

        code = new PermissionCode(candidate);
        return true;
    }

    /// <summary>Creates a code known to be well formed.</summary>
    /// <exception cref="ArgumentException">The code is malformed.</exception>
    public static PermissionCode Create(string candidate) =>
        TryCreate(candidate, out var code)
            ? code
            : throw new ArgumentException(
                $"'{candidate}' is not a permission code of the form resource.action.",
                nameof(candidate));

    /// <inheritdoc />
    public override string ToString() => Value;
}
