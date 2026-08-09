using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Domain.Modules.Auditing.Repositories;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore;

using AuditEventContract = MaintOrbit.Shared.Auditing.AuditEvent;
using AuditEventEntity = MaintOrbit.Domain.Modules.Auditing.Entities.AuditEvent;

namespace MaintOrbit.Infrastructure.Auditing;

/// <summary>
/// Writes Audit Events to <c>auditing.audit_events</c> — the documented store.
/// </summary>
/// <remarks>
/// <b>The only audit destination.</b> It replaced <c>LoggingAuditSink</c> rather than joining it:
/// §3.1 is explicit that "audit events implemented as log entries inherit log sampling and log
/// retention", so a second sink writing to the log would be a second store with weaker guarantees
/// and no way to tell which one an auditor should believe.
/// <para>
/// A failure to write is still logged, by <c>AuditTrail</c>, as an AU-8 incident — but that is an
/// alert about a lost record, not a copy of it, and it is deliberately not a fallback.
/// </para>
/// <para>
/// <b>Writes in its own transaction.</b> Every emission point calls this after the audited
/// operation has already committed, so this save carries the event alone. That ordering is what
/// makes ADR-0021's fail-open classification safe: a failure here cannot roll back the thing being
/// recorded, because there is nothing left to roll back.
/// </para>
/// </remarks>
internal sealed class PersistentAuditSink(
    IAuditEventRepository events,
    IUnitOfWork unitOfWork,
    MaintOrbitDbContext context)
    : IAuditSink
{
    public async Task WriteAsync(
        AuditEventContract auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var entity = AuditEventEntity.Record(
            auditEvent.OccurredAtUtc,
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.ActorType,
            auditEvent.CompanyId is { } company ? new CompanyId(company) : null,
            auditEvent.ActorEmployeeId,
            auditEvent.TargetType,
            auditEvent.TargetId,
            auditEvent.CorrelationId,
            auditEvent.Context);

        events.Add(entity);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Detached before the exception leaves, so a failed write does not stay in the change
            // tracker to be retried by whatever saves next. Without this, one rejected audit row
            // would attach itself to an unrelated later operation and fail that too — turning a
            // fail-open control into a cause of outages.
            context.Entry(entity).State = EntityState.Detached;
            throw;
        }
    }
}
