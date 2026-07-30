using MaintOrbit.Shared.Constants;

namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Environment checks for the environments the framework does not know about.
/// </summary>
/// <remarks>
/// The framework provides <c>IsDevelopment</c>, <c>IsStaging</c>, and <c>IsProduction</c>.
/// It has no concept of <c>Testing</c>, so that check lives here rather than as a string
/// comparison repeated wherever it is needed.
/// </remarks>
public static class HostEnvironmentExtensions
{
    /// <summary>
    /// Whether the host is running under automated test execution, including CI.
    /// </summary>
    public static bool IsTesting(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsEnvironment(EnvironmentNames.Testing);
    }
}
