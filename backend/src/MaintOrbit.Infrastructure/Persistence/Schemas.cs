namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// Module schema names.
/// </summary>
/// <remarks>
/// One schema per module (DB-P2), named for the module in <c>snake_case</c> (§1.5). Declared as
/// constants because <c>RequireExplicitSchema</c> makes every entity name its schema, and a
/// mistyped literal would create a real schema with a plausible name rather than fail.
/// <para>
/// Names are added as their modules gain tables. The full list of twelve is in
/// database-design §2; reproducing it here before the tables exist would be a second source of
/// truth that drifts.
/// </para>
/// </remarks>
internal static class Schemas
{
    /// <summary>Employees, credentials, sessions, roles, permissions, Platform API Keys.</summary>
    public const string Identity = "identity";
}
