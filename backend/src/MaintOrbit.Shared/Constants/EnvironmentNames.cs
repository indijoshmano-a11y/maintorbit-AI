namespace MaintOrbit.Shared.Constants;

/// <summary>
/// The environments MaintOrbit AI runs in.
/// </summary>
/// <remarks>
/// These names exist here, in <c>Shared</c>, so that every host — the API host today and
/// the Worker host later — resolves them from one place. Environment names scattered as
/// string literals across a codebase drift, and a mistyped literal fails silently by
/// simply never matching.
/// <para>
/// <c>Development</c>, <c>Staging</c>, and <c>Production</c> mirror the framework's own
/// names so that built-in helpers continue to behave as expected. <c>Testing</c> is
/// additional and has no framework equivalent — see
/// <c>docs/08-development/testing-strategy.md</c> §15.
/// </para>
/// </remarks>
public static class EnvironmentNames
{
    /// <summary>Local development on an engineer's machine.</summary>
    public const string Development = "Development";

    /// <summary>Automated test execution, including CI.</summary>
    public const string Testing = "Testing";

    /// <summary>Pre-release verification, running the production topology at smaller scale.</summary>
    public const string Staging = "Staging";

    /// <summary>Customer-facing production.</summary>
    public const string Production = "Production";

    /// <summary>
    /// Every recognised environment name, in deployment order.
    /// </summary>
    /// <remarks>
    /// Used by startup validation to reject an unrecognised <c>ASPNETCORE_ENVIRONMENT</c>
    /// rather than letting it silently fall through to production-like defaults.
    /// </remarks>
    public static readonly IReadOnlyList<string> All =
    [
        Development,
        Testing,
        Staging,
        Production
    ];
}
