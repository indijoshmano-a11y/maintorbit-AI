using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Health endpoint settings.
/// </summary>
/// <remarks>
/// NFR-OBS-005 requires that health endpoints <b>distinguish liveness from readiness</b>
/// and report dependency status. That distinction is the reason there are two paths rather
/// than one: liveness answers "is this process alive", readiness answers "should traffic be
/// routed here". Rolling deployment gates return-to-rotation on readiness, so conflating
/// them causes a host to receive traffic before its dependencies are reachable.
/// <para>
/// This milestone establishes the configuration shape only. The health checks themselves,
/// and their dependency probes, land in a later milestone.
/// </para>
/// </remarks>
public sealed class HealthCheckOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "HealthChecks";

    /// <summary>
    /// Whether health endpoints are exposed.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Path reporting whether the process is alive.
    /// </summary>
    /// <remarks>
    /// Answers only "is this process running". It must not probe dependencies — a liveness
    /// check that fails because a database is unreachable causes the orchestrator to
    /// restart a perfectly healthy process, turning a dependency outage into a restart loop.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^/[a-z0-9\-/]+$", ErrorMessage = "LivenessPath must be a lowercase absolute path.")]
    public string LivenessPath { get; init; } = "/health/live";

    /// <summary>
    /// Path reporting whether this instance should receive traffic.
    /// </summary>
    /// <remarks>
    /// Probes dependencies. This is what gates return-to-rotation during a rolling
    /// deployment.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^/[a-z0-9\-/]+$", ErrorMessage = "ReadinessPath must be a lowercase absolute path.")]
    public string ReadinessPath { get; init; } = "/health/ready";
}
