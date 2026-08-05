using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Abstractions.Auditing;

/// <summary>
/// Records what happened, for the auditing module to keep.
/// </summary>
/// <remarks>
/// <b>Emission only.</b> Identity produces audit events; <c>auditing</c> owns the append-only store
/// (§4.2), its monthly partitions, and its retention. ADR-0002 permits a module to reference
/// another's published contracts and nothing else, so this port takes
/// <see cref="AuditEvent"/> — a Shared contract — and knows nothing about where it goes.
/// <para>
/// <b>Fail-open, and never silently.</b> SD-004 classifies audit emission fail-open so a platform
/// fault never becomes a customer outage; AU-8 makes a failure to write "an incident — recorded,
/// alerted, reconciled". An implementation therefore must not throw, and must not swallow: the
/// operation being audited succeeds, and the failure to record it is loud.
/// </para>
/// <para>
/// <b>Handlers do not decide <i>whether</i> to emit.</b> §3.3 puts emission at pipeline position 8
/// so coverage is not "a function of developer discipline" — the ADR-0012 pipeline that would do
/// that is not built, so handlers call this directly, exactly as §3.3 notes the Gateway hot path
/// does when it bypasses the pipeline. What keeps discipline honest meanwhile is a test that
/// asserts every documented event is emitted.
/// </para>
/// </remarks>
public interface IAuditTrail
{
    /// <summary>
    /// Records an event, filling in the ambient actor, tenant, correlation, and time.
    /// </summary>
    /// <remarks>
    /// The caller supplies what only it knows — the action, the target, the outcome, and any small
    /// non-content facts. Who and when come from the request, so no call site can record a
    /// different actor than the one that made it.
    /// </remarks>
    /// <param name="action">One of <see cref="AuditActions"/>.</param>
    /// <param name="outcome">How the action ended.</param>
    /// <param name="targetType">One of <see cref="AuditTargets"/>, when there is a target.</param>
    /// <param name="targetId">Which target.</param>
    /// <param name="context">Small non-content facts. Never prompt or completion content (AU-4).</param>
    Task RecordAsync(
        string action,
        AuditOutcome outcome,
        string? targetType = null,
        string? targetId = null,
        IReadOnlyDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an event for an actor the request has not established.
    /// </summary>
    /// <remarks>
    /// A failed sign-in is the case this exists for: the attempt is exactly what must be audited
    /// (FR-AUTH-014), and at that point there is no validated identity to read. The caller names
    /// the actor it managed to resolve, or none.
    /// </remarks>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
