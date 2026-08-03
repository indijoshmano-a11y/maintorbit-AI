namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// The stable identifier of a role.
/// </summary>
/// <remarks>
/// Text for the same reason as <see cref="PermissionCode"/> (§1.6), and because it is already
/// referenced by name elsewhere — <c>invitations.role_code</c> assigns a role at invitation (§4.1).
/// <para>
/// <b>Nothing branches on this value.</b> SD-020 makes roles permission presets, not code
/// branches, and CLAUDE.md lists branching authorization on a role name under things never to do.
/// A role exists to carry a set of permissions; every decision is made against the permissions.
/// </para>
/// </remarks>
public readonly record struct RoleCode
{
    /// <summary>Longest code accepted.</summary>
    public const int MaxLength = 64;

    private RoleCode(string value) => Value = value;

    /// <summary>The code.</summary>
    public string Value { get; }

    /// <summary>Whether this code was never set.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Creates a code from a candidate, if it is well formed.</summary>
    public static bool TryCreate(string? candidate, out RoleCode code)
    {
        code = default;

        if (candidate is null || candidate.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        code = new RoleCode(candidate);
        return true;
    }

    /// <summary>Creates a code known to be well formed.</summary>
    /// <exception cref="ArgumentException">The code is malformed.</exception>
    public static RoleCode Create(string candidate) =>
        TryCreate(candidate, out var code)
            ? code
            : throw new ArgumentException($"'{candidate}' is not a role code.", nameof(candidate));

    /// <inheritdoc />
    public override string ToString() => Value;
}
