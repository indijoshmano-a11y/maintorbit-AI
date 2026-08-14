using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Modules.Auditing.Queries;

/// <summary>
/// One Audit Event as the API returns it.
/// </summary>
/// <remarks>
/// <b>A projection, not the aggregate.</b> The fields are exactly `06-database` §4.10's key
/// columns, and nothing else: no <c>stream_entry_id</c> (an ingestion detail with no meaning to a
/// reader), no internal identifiers beyond the event's own.
/// <para>
/// <see cref="Context"/> carries only what the domain's sanitizer already allowed through, so a
/// credential-shaped value was redacted before the row was written and cannot appear here. §3.15
/// also fixes what is absent: <b>never prompt or completion content</b> (AU-4).
/// </para>
/// </remarks>
public sealed record AuditEventView(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string Action,
    string Outcome,
    string ActorType,
    string? ActorEmployeeId,
    string? TargetType,
    string? TargetId,
    string? CorrelationId,
    IReadOnlyDictionary<string, string>? Context);

/// <summary>
/// A page of Audit Events, in the documented collection envelope (§4.4).
/// </summary>
/// <remarks>
/// <b>No total count, deliberately.</b> §4.4 excludes it from ledger, audit, and analytics
/// collections: counting matched rows across partitions costs as much as the query. It also
/// happens to close a side channel — a total would let a caller probe for the existence of rows
/// they cannot read, if the isolation were ever weaker than it is.
/// </remarks>
public sealed record AuditEventPage(
    IReadOnlyList<AuditEventView> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Searches the Company's Audit Events (AU-5, FR-AUD-005).
/// </summary>
/// <remarks>
/// <b>There is no Company on this query, and that is the security design.</b> The tenant comes
/// from the validated token through the request's tenant scope, and row-level security applies it
/// in the database. A <c>companyId</c> parameter would be exactly the "switch company" input
/// TC-1 forbids — and would be the first thing an attacker tried.
/// <para>
/// The filters are §3.15's and no more: "search is <b>structured filtering</b>, not full-text".
/// Every one maps to a documented column, and there is no operator syntax, no free-text term, and
/// no way to express a predicate the indexes cannot serve.
/// </para>
/// </remarks>
public sealed record SearchAuditEventsQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Action = null,
    string? Outcome = null,
    Guid? ActorEmployeeId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? CorrelationId = null,
    int? PageSize = null,
    string? Cursor = null) : IQuery<AuditEventPage>;

/// <summary>
/// Limits from `api-specification` §5.5, applied to every audit read.
/// </summary>
public static class AuditSearchLimits
{
    /// <summary>Default page size (§5.5).</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maximum page size (§5.5).</summary>
    public const int MaximumPageSize = 200;

    /// <summary>
    /// Maximum span of a single query, in days (§5.5 — "Time range on ledger queries").
    /// </summary>
    /// <remarks>
    /// Required, not optional. An unbounded range would scan every partition, which is the query
    /// shape partitioning exists to avoid — and it is also how a caller would ask for the entire
    /// audit history in one request.
    /// </remarks>
    public const int MaximumRangeDays = 90;

    /// <summary>
    /// Validates the range and page size, returning the effective page size.
    /// </summary>
    /// <remarks>
    /// Shared by search and export so the two cannot drift into different limits — an export that
    /// accepted a wider range than search would be a way around the search limit.
    /// </remarks>
    public static Result<int> Validate(DateTimeOffset fromUtc, DateTimeOffset toUtc, int? pageSize)
    {
        if (toUtc <= fromUtc)
        {
            return Result.Failure<int>(
                Error.Validation("The end of the range must be after its start."));
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(MaximumRangeDays))
        {
            return Result.Failure<int>(Error.Validation(
                $"The range may span at most {MaximumRangeDays} days. Narrow it and page through."));
        }

        var size = pageSize ?? DefaultPageSize;

        if (size is < 1 or > MaximumPageSize)
        {
            return Result.Failure<int>(Error.Validation(
                $"pageSize must be between 1 and {MaximumPageSize}."));
        }

        return Result.Success(size);
    }

    /// <summary>Whether an outcome names one of the three documented values.</summary>
    public static bool IsKnownOutcome(string outcome) =>
        Enum.TryParse<AuditOutcome>(outcome, ignoreCase: false, out _);
}

/// <summary>Reads Audit Events. Implemented in infrastructure; no <c>IQueryable</c> escapes it.</summary>
/// <remarks>
/// <b>There is no write member, and there never will be.</b> AU-1 is structural: the read contract
/// and the write contract are separate types precisely so neither can grow the other's capability
/// by accident.
/// </remarks>
public interface IAuditEventReader
{
    /// <summary>Returns one page, newest first.</summary>
    Task<AuditEventPage> SearchAsync(
        AuditEventFilter filter, int pageSize, AuditCursor? after, CancellationToken cancellationToken);

    /// <summary>
    /// Streams every matching event, newest first, without materialising the set.
    /// </summary>
    /// <remarks>
    /// <c>IAsyncEnumerable</c> so the export writes rows as the reader produces them. Buffering an
    /// audit range into a list before sending it would put an unbounded, customer-controlled
    /// allocation in the request path.
    /// </remarks>
    IAsyncEnumerable<AuditEventView> StreamAsync(
        AuditEventFilter filter, int maximumRows, CancellationToken cancellationToken);
}

/// <summary>The validated filter, shared by search and export.</summary>
public sealed record AuditEventFilter(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Action,
    AuditOutcome? Outcome,
    Guid? ActorEmployeeId,
    string? TargetType,
    string? TargetId,
    string? CorrelationId)
{
    /// <summary>
    /// A stable fingerprint of the filter, carried in the cursor.
    /// </summary>
    /// <remarks>
    /// §5.4: "Filter changes invalidate the cursor — an error, not silent misbehaviour". Without
    /// this, a caller who changed a filter mid-page would receive a page ordered by the old query's
    /// keyset and silently miss rows. Comparing a fingerprint makes that a refusal.
    /// </remarks>
    public string Fingerprint() =>
        string.Join(
            '|',
            FromUtc.UtcTicks, ToUtc.UtcTicks, Action, Outcome, ActorEmployeeId,
            TargetType, TargetId, CorrelationId)
        .GetHashCode(StringComparison.Ordinal)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
}
