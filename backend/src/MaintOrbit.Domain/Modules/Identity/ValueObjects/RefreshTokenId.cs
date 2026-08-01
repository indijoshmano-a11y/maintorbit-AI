namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>Identifies a refresh token record.</summary>
/// <remarks>
/// The record's identifier, not the token itself. The token is a secret the client holds; this is
/// the row it corresponds to, and it appears in <c>superseded_by_id</c> to chain a rotation.
/// </remarks>
public readonly record struct RefreshTokenId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static RefreshTokenId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static RefreshTokenId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
