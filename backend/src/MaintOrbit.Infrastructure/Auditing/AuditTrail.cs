using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.Auditing;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Infrastructure.Auditing;

/// <summary>
/// Fills in the ambient context and hands the event to the sink, fail-open.
/// </summary>
/// <remarks>
/// <b>The actor is never taken from a caller.</b> It comes from the validated token through
/// <see cref="ICurrentIdentity"/>, so no call site can record an action against somebody else —
/// an audit trail that could name the wrong actor is worse than none, because it is believed.
/// <para>
/// <b>Nothing here throws.</b> SD-004 makes audit emission fail-open: the operation being audited
/// has already happened, and failing the request would turn a bookkeeping fault into a customer
/// outage. AU-8 makes the failure an incident instead — logged at error with the action that went
/// unrecorded, so reconciliation has something to find.
/// </para>
/// </remarks>
internal sealed partial class AuditTrail(
    IAuditSink sink,
    ICurrentIdentity currentIdentity,
    ICorrelationIdAccessor correlation,
    ILogger<AuditTrail> logger,
    TimeProvider timeProvider)
    : IAuditTrail
{
    /// <inheritdoc />
    public Task RecordAsync(
        string action,
        AuditOutcome outcome,
        string? targetType = null,
        string? targetId = null,
        IReadOnlyDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            new AuditEvent(
                timeProvider.GetUtcNow(),
                action,
                outcome,
                currentIdentity.EmployeeId is null ? AuditActorType.Anonymous : AuditActorType.Employee,
                currentIdentity.CompanyId?.Value,
                currentIdentity.EmployeeId?.Value,
                targetType,
                targetId,
                correlation.Current,
                context),
            cancellationToken);

    /// <inheritdoc />
    public async Task RecordAsync(
        AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // The correlation identifier is filled here rather than by the caller, so every record
        // carries one and none carries somebody else's.
        var enriched = auditEvent.CorrelationId is null
            ? auditEvent with { CorrelationId = correlation.Current }
            : auditEvent;

        try
        {
            await sink.WriteAsync(enriched, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NotRecorded(logger, enriched.Action, enriched.Outcome.ToString(), error);
        }
    }

    [LoggerMessage(
        EventId = 1600,
        Level = LogLevel.Error,
        Message = "Audit event {Action} ({Outcome}) could not be recorded. The operation itself " +
                  "succeeded; this is an AU-8 incident and must be reconciled.")]
    private static partial void NotRecorded(
        ILogger logger, string action, string outcome, Exception error);
}
