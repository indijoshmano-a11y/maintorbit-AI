namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// Identifies the authenticated session an access token was issued for.
/// </summary>
/// <remarks>
/// The identifier only. Sessions themselves — device label, idle and absolute expiry, revocation
/// — are a later milestone; this exists because SD-013 lists session among the access token's
/// claims, and a token cannot carry a session reference without a type to express it.
/// <para>
/// It is what makes revocation possible at all: permissions are resolved server-side per request
/// and checked against a tombstone, and the tombstone is keyed by session. A token without this
/// claim could not be revoked before it expired.
/// </para>
/// </remarks>
public readonly record struct SessionId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static SessionId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static SessionId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
