namespace MaintOrbit.Domain.Modules.Identity.ValueObjects;

/// <summary>
/// Identifies an Employee's password credential.
/// </summary>
/// <remarks>
/// UUIDv7, generated in the application, for the same reasons as <see cref="EmployeeId"/>
/// (§1.6, and TD-5 remains open).
/// <para>
/// Distinct from <see cref="EmployeeId"/> even though the relationship is one-to-at-most-one.
/// Both are UUIDs, so reusing the Employee's identifier as the credential's key would compile
/// everywhere and make the two mutually assignable at every call site that takes a
/// <c>Guid</c> — on the one table where a mix-up means reading another account's hash.
/// </remarks>
public readonly record struct EmployeeCredentialId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    public static EmployeeCredentialId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static EmployeeCredentialId New() => new(Guid.CreateVersion7());

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
