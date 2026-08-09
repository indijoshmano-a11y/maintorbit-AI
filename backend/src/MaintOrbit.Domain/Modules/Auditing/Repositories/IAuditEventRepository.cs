using MaintOrbit.Domain.Modules.Auditing.Entities;

namespace MaintOrbit.Domain.Modules.Auditing.Repositories;

/// <summary>
/// Writes Audit Events.
/// </summary>
/// <remarks>
/// <b>One method, and no read.</b> That is not an oversight — it is AU-1 expressed in the shape of
/// the interface. There is no <c>Update</c>, no <c>Remove</c>, and no <c>Find</c> that could be
/// followed by either. A generic repository would have supplied all three by inheritance, which is
/// one reason this codebase does not have one.
/// <para>
/// Reads arrive with the audit query API (AU-5, AU-6) and belong on a separate read contract with
/// keyset pagination (DD-13). Adding them here would put the search surface and the write surface
/// behind one interface, and the write surface is the one with the immutability guarantee.
/// </para>
/// </remarks>
public interface IAuditEventRepository
{
    /// <summary>
    /// Stages an event for insertion.
    /// </summary>
    /// <remarks>
    /// Staging only — the unit of work decides when it is written. Emission happens after the
    /// audited operation has committed, so the event is written in its own transaction and a
    /// failure to record cannot roll back the thing being recorded (ADR-0021 fail-open, AU-8).
    /// </remarks>
    void Add(AuditEvent auditEvent);
}
