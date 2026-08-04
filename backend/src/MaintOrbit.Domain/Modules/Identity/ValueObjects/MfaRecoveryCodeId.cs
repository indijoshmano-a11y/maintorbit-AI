namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>Identifies a single recovery code.</summary>
/// <remarks>
/// The row's identifier, not the code. The code exists once, when the set is shown to the
/// Employee, and only its digest persists.
/// </remarks>
public readonly record struct MfaRecoveryCodeId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static MfaRecoveryCodeId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier (§1.6).</summary>
    public static MfaRecoveryCodeId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
