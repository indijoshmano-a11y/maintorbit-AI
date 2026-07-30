namespace MaintOrbit.Api.HealthChecks;

/// <summary>
/// Tags selecting which checks run at which health endpoint.
/// </summary>
/// <remarks>
/// NFR-OBS-005 requires liveness and readiness to be distinguishable. Tagging is how that
/// distinction is expressed: a check is written once and declares which question it answers,
/// rather than the two endpoints maintaining separate lists that drift apart.
/// </remarks>
public static class HealthCheckTags
{
    /// <summary>
    /// Marks a check as gating whether this instance should receive traffic.
    /// </summary>
    /// <remarks>
    /// Every dependency probe — database, cache, message transport — carries this tag.
    /// A check <b>without</b> it never runs at the readiness endpoint, and a check
    /// <b>with</b> it must never be added to liveness: a liveness probe that fails on an
    /// unreachable dependency causes the orchestrator to restart a healthy process, turning
    /// a dependency outage into a restart loop across every instance at once.
    /// </remarks>
    public const string Ready = "ready";
}
