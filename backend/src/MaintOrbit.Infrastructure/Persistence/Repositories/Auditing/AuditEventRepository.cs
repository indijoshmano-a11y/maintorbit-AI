using MaintOrbit.Domain.Modules.Auditing.Entities;
using MaintOrbit.Domain.Modules.Auditing.Repositories;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Auditing;

/// <summary>
/// Stages Audit Events for insertion.
/// </summary>
/// <remarks>
/// One line, deliberately. The interface offers no read and no mutation, so there is nothing else
/// to implement — and anything more here would be a capability the append-only guarantee does not
/// permit.
/// </remarks>
internal sealed class AuditEventRepository(MaintOrbitDbContext context) : IAuditEventRepository
{
    public void Add(AuditEvent auditEvent) => context.AuditEvents.Add(auditEvent);
}
