using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Abstractions.Auditing;

/// <summary>
/// Where audit events go.
/// </summary>
/// <remarks>
/// Split from <see cref="IAuditTrail"/> deliberately. The trail is what handlers call and is
/// responsible for the ambient context and the fail-open rule; the sink is the destination, and is
/// the single seam the <c>auditing</c> module replaces.
/// <para>
/// §3.3 gives the target shape — pipeline → durable stream → batch writer → append-only store —
/// so the eventual implementation writes to a Redis Stream (ADR-0006's third role) and a worker
/// drains it. Neither exists, and neither belongs to identity.
/// </para>
/// <para>
/// <b>It may throw.</b> Fail-open is the trail's rule, not the sink's: a sink that swallowed its
/// own failures would make AU-8's incident unobservable.
/// </para>
/// </remarks>
public interface IAuditSink
{
    /// <summary>Writes one event.</summary>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
