using System.Runtime.CompilerServices;
using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Modules.Auditing.Queries;
using Microsoft.EntityFrameworkCore;

using AuditEventEntity = MaintOrbit.Domain.Modules.Auditing.Entities.AuditEvent;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Auditing;

/// <summary>
/// Reads Audit Events through the request's tenant-scoped <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <b>The request's context, never the elevated connection.</b> `ElevatedCredentialDirectory` exists
/// for the four authentication lookups that must precede a tenant being known
/// (`04-tenant-security` §3.4 path 13); an audit read is the opposite case — the caller is
/// authenticated, the tenant is established, and reading through anything else would remove the
/// control that makes the answer correct.
/// <para>
/// <b>Isolation is the database's, not this class's.</b> There is no <c>Where(e =&gt; e.CompanyId
/// == ...)</c> below. `rls_audit_events_read` filters `SELECT` against
/// <c>app.current_company_id</c>, which the connection interceptor sets from the validated token —
/// so a defect in this file cannot widen what it returns, which is exactly what NFR-SEC-007
/// requires. An application-side filter would look like security and would be the only thing
/// standing between a mistake here and another Company's evidence.
/// </para>
/// <para>
/// No <c>IQueryable</c> leaves this type. The port returns materialised views, so a caller cannot
/// compose a predicate onto a query whose safety depends on how it was built.
/// </para>
/// </remarks>
internal sealed class AuditEventReader(MaintOrbitDbContext context, TimeProvider timeProvider)
    : IAuditEventReader
{
    public async Task<AuditEventPage> SearchAsync(
        AuditEventFilter filter,
        int pageSize,
        AuditCursor? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // One more than asked for. That is how `hasMore` is answered without a count — §4.4 removes
        // `totalCount` from audit collections because counting across partitions costs as much as
        // the query, and a row that is fetched and discarded costs one row.
        var rows = await Filtered(filter, after)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows.Take(pageSize).ToList() : rows;

        string? nextCursor = null;

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];

            nextCursor = new AuditCursor(
                    last.OccurredAtUtc, last.Id.Value, filter.Fingerprint())
                .Encode(timeProvider.GetUtcNow());
        }

        return new AuditEventPage([.. page.Select(Project)], nextCursor, hasMore);
    }

    public async IAsyncEnumerable<AuditEventView> StreamAsync(
        AuditEventFilter filter,
        int maximumRows,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // AsAsyncEnumerable, not ToListAsync: rows reach the response as the reader produces them.
        // Materialising an export range first would put an unbounded, caller-chosen allocation in
        // the request path — and the caller chooses the range.
        var query = Filtered(filter, after: null)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(maximumRows)
            .AsAsyncEnumerable();

        await foreach (var row in query.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return Project(row);
        }
    }

    /// <summary>
    /// The filtered, keyset-positioned query.
    /// </summary>
    /// <remarks>
    /// <b>The predicates are written to match the index, not merely to be correct.</b> The range on
    /// <c>occurred_at_utc</c> comes first so the planner can prune partitions and seek within
    /// `ix_audit_events_company_id_occurred_at_utc`; the keyset comparison is the standard
    /// <c>(a &lt; x) OR (a = x AND b &lt; y)</c> form over the same two columns the ordering uses.
    /// <para>
    /// A keyset written over different columns from the <c>ORDER BY</c> compiles, returns
    /// plausible-looking pages, and silently skips rows — which is why the ordering, the cursor
    /// composition, and this predicate are all stated as <c>(occurredAtUtc, id)</c> and tested
    /// against a page boundary rather than a page count.
    /// </remarks>
    private IQueryable<AuditEventEntity> Filtered(AuditEventFilter filter, AuditCursor? after)
    {
        // ---- The keyset, in raw SQL, and only the keyset -------------------------------------
        //
        // EF cannot translate this predicate. `Id` is an `AuditEventId` behind a ValueConverter to
        // `uuid`: equality against the value object translates, and ORDER BY translates, but
        // `e.Id.Value < x` reaches *through* the converter, and the provider has no expression for
        // that. The proven error is:
        //
        //   The LINQ expression '... a.Id.Value < @after_Id' could not be translated.
        //
        // The alternatives were worse. Giving `AuditEventId` comparison operators would add
        // ordering semantics to a domain type solely to satisfy a query provider. A shadow Guid
        // property would duplicate the primary key in the model. Dropping the tie-break would make
        // the ordering non-deterministic when two events share a timestamp — which §5.4 forbids and
        // which happens routinely, because a single request emits several events within one
        // microsecond.
        //
        // So the range and the keyset are expressed as SQL and everything else stays LINQ. Both
        // are parameterised — FromSqlInterpolated sends parameters, never concatenated text — and
        // the query runs on the request's own connection, so `rls_audit_events_read` still decides
        // which rows exist. Nothing here is elevated and nothing filters by Company in application
        // code.
        var query = after is null
            ? context.AuditEvents.FromSql(
                $"""
                 SELECT * FROM auditing.audit_events
                 WHERE occurred_at_utc >= {filter.FromUtc} AND occurred_at_utc < {filter.ToUtc}
                 """)
            : context.AuditEvents.FromSql(
                $"""
                 SELECT * FROM auditing.audit_events
                 WHERE occurred_at_utc >= {filter.FromUtc} AND occurred_at_utc < {filter.ToUtc}
                   AND (occurred_at_utc < {after.OccurredAtUtc}
                        OR (occurred_at_utc = {after.OccurredAtUtc} AND id < {after.Id}))
                 """);

        query = query.AsNoTracking();

        // Everything below is ordinary LINQ, composed over the SQL above as a subquery. These
        // translate without difficulty, and keeping them here means the filter set stays readable
        // and cannot drift out of step with the validated filter object.
        if (filter.Action is { } action)
        {
            query = query.Where(e => e.Action == action);
        }

        if (filter.Outcome is { } outcome)
        {
            query = query.Where(e => e.Outcome == outcome);
        }

        if (filter.ActorEmployeeId is { } actor)
        {
            query = query.Where(e => e.ActorEmployeeId == actor);
        }

        if (filter.TargetType is { } targetType)
        {
            query = query.Where(e => e.TargetType == targetType);
        }

        if (filter.TargetId is { } targetId)
        {
            query = query.Where(e => e.TargetId == targetId);
        }

        if (filter.CorrelationId is { } correlationId)
        {
            query = query.Where(e => e.CorrelationId == correlationId);
        }

        return query;
    }

    /// <summary>
    /// Maps to the documented representation.
    /// </summary>
    /// <remarks>
    /// <c>stream_entry_id</c> is deliberately absent: it is an ingestion detail (DD-6) that means
    /// nothing to a reader and is null on every row today. Everything else is §4.10's key columns.
    /// The context passes through as stored — the domain's sanitizer redacted credential-shaped
    /// values before the row existed, so there is nothing left here to strip.
    /// </remarks>
    private static AuditEventView Project(AuditEventEntity e) =>
        new(
            e.Id.ToString(),
            e.OccurredAtUtc,
            e.Action,
            e.Outcome.ToString(),
            e.ActorType.ToString(),
            e.ActorEmployeeId?.ToString("n"),
            e.TargetType,
            e.TargetId,
            e.CorrelationId,
            e.Context);
}
