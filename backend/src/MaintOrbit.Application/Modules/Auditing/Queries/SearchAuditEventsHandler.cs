using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Modules.Auditing.Queries;

/// <summary>
/// Validates a search, then reads one page.
/// </summary>
/// <remarks>
/// Validation is here rather than in the endpoint so search and export apply the same rules
/// through the same code — an export that accepted a wider range than search would be a way around
/// the search limit rather than a separate feature.
/// </remarks>
public sealed class SearchAuditEventsQueryHandler(
    IAuditEventReader reader,
    TimeProvider timeProvider)
    : IQueryHandler<SearchAuditEventsQuery, AuditEventPage>
{
    public async Task<Result<AuditEventPage>> HandleAsync(
        SearchAuditEventsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var built = AuditSearchFilterFactory.Build(query.FromUtc, query.ToUtc, query.Action,
            query.Outcome, query.ActorEmployeeId, query.TargetType, query.TargetId,
            query.CorrelationId, query.PageSize);

        if (built.IsFailure)
        {
            return Result.Failure<AuditEventPage>(built.Error);
        }

        var (filter, pageSize) = built.Value;

        AuditCursor? after = null;

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            // A cursor that does not decode, has expired, or was issued for a different filter is
            // refused rather than ignored. §5.4: "an error, not silent misbehaviour" — silently
            // restarting from the first page would look like duplicated results, and silently
            // continuing with a stale keyset would skip rows nobody knew were missing.
            if (!AuditCursor.TryDecode(
                    query.Cursor, filter.Fingerprint(), timeProvider.GetUtcNow(), out after))
            {
                return Result.Failure<AuditEventPage>(Error.Validation(
                    "The cursor is not valid for this query. Cursors expire, and changing a " +
                    "filter invalidates them — start again from the first page."));
            }
        }

        var page = await reader.SearchAsync(filter, pageSize, after, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(page);
    }
}

/// <summary>
/// Turns raw search input into a validated filter.
/// </summary>
/// <remarks>
/// Shared by the search handler and the export endpoint. The filter is the only thing that reaches
/// the reader, so a value that fails here cannot reach a query — including an outcome outside the
/// three documented values, which would otherwise silently match nothing and read as "no events".
/// </remarks>
public static class AuditSearchFilterFactory
{
    /// <summary>Validates and builds, or explains why not.</summary>
    public static Result<(AuditEventFilter Filter, int PageSize)> Build(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? action,
        string? outcome,
        Guid? actorEmployeeId,
        string? targetType,
        string? targetId,
        string? correlationId,
        int? pageSize)
    {
        var limits = AuditSearchLimits.Validate(fromUtc, toUtc, pageSize);

        if (limits.IsFailure)
        {
            return Result.Failure<(AuditEventFilter, int)>(limits.Error);
        }

        AuditOutcome? parsedOutcome = null;

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!AuditSearchLimits.IsKnownOutcome(outcome))
            {
                return Result.Failure<(AuditEventFilter, int)>(Error.Validation(
                    "outcome must be Success, Failure, or Denied."));
            }

            parsedOutcome = Enum.Parse<AuditOutcome>(outcome);
        }

        var filter = new AuditEventFilter(
            fromUtc.ToUniversalTime(),
            toUtc.ToUniversalTime(),
            Trimmed(action),
            parsedOutcome,
            actorEmployeeId,
            Trimmed(targetType),
            Trimmed(targetId),
            Trimmed(correlationId));

        return Result.Success((filter, limits.Value));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
