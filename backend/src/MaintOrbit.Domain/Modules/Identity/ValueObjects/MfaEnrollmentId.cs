namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>Identifies an MFA enrolment.</summary>
public readonly record struct MfaEnrollmentId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static MfaEnrollmentId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier (§1.6).</summary>
    public static MfaEnrollmentId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
