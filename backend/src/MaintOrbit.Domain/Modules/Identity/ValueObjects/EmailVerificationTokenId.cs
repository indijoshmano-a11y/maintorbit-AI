namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>Identifies an email verification request.</summary>
/// <remarks>
/// The row's identifier, not the token. The token is the secret that reaches the Employee by
/// email and is never stored; this is the record of the request it belongs to.
/// </remarks>
public readonly record struct EmailVerificationTokenId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static EmailVerificationTokenId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier (§1.6).</summary>
    public static EmailVerificationTokenId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
