namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// Groups every refresh token descended from one authentication.
/// </summary>
/// <remarks>
/// The unit of reuse detection. SD-014 rotates on every use, so a session produces a chain of
/// tokens; they all carry the same family. Presenting a token that has already been used revokes
/// <b>the entire family</b>, not just that token — because a used token appearing twice means two
/// parties hold it, and there is no way to tell which one is the legitimate client.
/// </remarks>
public readonly record struct RefreshTokenFamilyId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static RefreshTokenFamilyId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Starts a new family — one authentication, one family.</summary>
    public static RefreshTokenFamilyId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
