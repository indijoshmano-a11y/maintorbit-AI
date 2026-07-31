namespace MaintOrbit.Shared.MultiTenancy;

/// <summary>
/// Identifies the Company a row belongs to — the tenant discriminator.
/// </summary>
/// <remarks>
/// Lives in the shared kernel rather than in the tenancy module because every tenant-scoped
/// relation in every schema carries it (DB-P1), and AT-4 requires it on every tenant-scoped
/// entity. A type owned by one module would have to be referenced by all eleven others, which
/// is precisely the cross-module coupling ADR-0002 forbids.
/// <para>
/// Strongly typed so it cannot be transposed with any other identifier. Every identifier in this
/// system is a UUID, so <c>Guid</c> parameters are mutually assignable — and the one place that
/// matters most is the tenant discriminator, where a transposition reads rows for the wrong
/// Company.
/// </para>
/// </remarks>
public readonly record struct CompanyId(Guid Value)
{
    /// <summary>An unset identifier.</summary>
    /// <remarks>
    /// Reachable because a struct always has a default. Callers guard against it rather than
    /// pretend it cannot occur — <see cref="IsEmpty"/> makes the check explicit.
    /// </remarks>
    public static CompanyId Empty => default;

    /// <summary>Whether this identifier was never set.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Formatted without hyphens, matching how identifiers appear in logs and URLs.</summary>
    public override string ToString() => Value.ToString("n");
}
