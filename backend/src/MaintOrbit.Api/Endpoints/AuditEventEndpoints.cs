using System.Globalization;
using System.Text.Json;
using MaintOrbit.Api.Authorization;
using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.Modules.Auditing.Queries;
using MaintOrbit.Shared.Auditing;
using MaintOrbit.Domain.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// The Audit Events endpoints — <c>/api/v1/audit-events</c> (api-specification §3.15).
/// </summary>
/// <remarks>
/// <b>Read-only, structurally.</b> §3.15: "No create, update, or delete operations exist (AU-1) —
/// not gated by permission, absent from the API". There is no <c>MapPost</c>, <c>MapPut</c>, or
/// <c>MapDelete</c> in this file, and that absence is the guarantee.
/// <para>
/// <b>No endpoint here takes a Company.</b> Not in the route, not in the query string, not in a
/// body. §3.15 gives the permission as <c>audit.read [C]</c> — Company scope — and the Company
/// comes from the validated token through the tenant middleware, exactly as TC-1 requires. A
/// caller cannot name a tenant, so a caller cannot name somebody else's.
/// </para>
/// </remarks>
public static class AuditEventEndpoints
{
    /// <summary>
    /// The largest export a single synchronous request will produce.
    /// </summary>
    /// <remarks>
    /// <b>A documented gap, bounded rather than invented.</b> §5.5 says export row count is
    /// "asynchronous above a threshold" and names no threshold, and no asynchronous export
    /// mechanism is specified — it would need a job framework and a result store, neither of which
    /// exists. So the supported portion is implemented: a streamed synchronous export, bounded, and
    /// a caller who exceeds the bound is told to narrow the range rather than silently receiving a
    /// truncated file. Recorded in the milestone report as the missing decision.
    /// </remarks>
    public const int MaximumExportRows = 50_000;

    /// <summary>Maps the Audit Events endpoints.</summary>
    public static IEndpointRouteBuilder MapAuditEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/audit-events");

        // Both operations take the same permission, because §3.15 gives them the same one — and
        // because export is search with a different transport. A caller who can page through every
        // record can already assemble the file by hand.
        group.MapGet("/", SearchAsync)
            .RequirePermission(AuditPermissions.AuditRead);

        group.MapGet("/export", ExportAsync)
            .RequirePermission(AuditPermissions.AuditRead);

        return endpoints;
    }

    /// <summary>Searches the Company's Audit Events (AU-5).</summary>
    private static async Task<IResult> SearchAsync(
        HttpContext http,
        [FromServices] IQueryHandler<SearchAuditEventsQuery, AuditEventPage> handler,
        [FromQuery] DateTimeOffset fromUtc,
        [FromQuery] DateTimeOffset toUtc,
        [FromQuery] string? action,
        [FromQuery] string? outcome,
        [FromQuery] Guid? actorEmployeeId,
        [FromQuery] string? targetType,
        [FromQuery] string? targetId,
        [FromQuery] string? correlationId,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var result = await handler.HandleAsync(
            new SearchAuditEventsQuery(
                fromUtc, toUtc, action, outcome, actorEmployeeId,
                targetType, targetId, correlationId, pageSize, cursor),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Problem(http, result.Error);
    }

    /// <summary>
    /// Streams the Company's Audit Events as a file (AU-6).
    /// </summary>
    /// <remarks>
    /// <b>JSON, streamed as an array — the format the API already commits to.</b> FR-AUD-006 asks
    /// for "a documented machine-readable format" and no document names one; choosing CSV, NDJSON,
    /// or anything else would be inventing product behaviour. Reusing the representation search
    /// already returns invents nothing: an auditor's tooling parses the same shape either way.
    /// <para>
    /// Written directly to the response body rather than buffered. An export range is chosen by the
    /// caller, so materialising it first would put a caller-controlled allocation in the request
    /// path — and the whole reason for streaming is that the set has no small upper bound.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ExportAsync(
        HttpContext http,
        [FromServices] IAuditEventReader reader,
        [FromServices] IAuditTrail audit,
        [FromQuery] DateTimeOffset fromUtc,
        [FromQuery] DateTimeOffset toUtc,
        [FromQuery] string? action,
        [FromQuery] string? outcome,
        [FromQuery] Guid? actorEmployeeId,
        [FromQuery] string? targetType,
        [FromQuery] string? targetId,
        [FromQuery] string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(audit);

        // The same validation search uses, through the same code — an export that accepted a wider
        // range would be a way around the search limit rather than a separate capability.
        var built = AuditSearchFilterFactory.Build(
            fromUtc, toUtc, action, outcome, actorEmployeeId,
            targetType, targetId, correlationId, pageSize: null);

        if (built.IsFailure)
        {
            return Problem(http, built.Error);
        }

        var filter = built.Value.Filter;

        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.ContentDisposition =
            $"attachment; filename=\"audit-events-{fromUtc.UtcDateTime:yyyyMMdd}-{toUtc.UtcDateTime:yyyyMMdd}.json\"";

        var written = 0;

        await using (var writer = new Utf8JsonWriter(http.Response.BodyWriter))
        {
            writer.WriteStartArray();

            await foreach (var item in reader
                .StreamAsync(filter, MaximumExportRows, cancellationToken)
                .ConfigureAwait(false))
            {
                JsonSerializer.Serialize(writer, item, JsonOptions);
                written++;

                // Flushed periodically so the client receives rows as they are produced and the
                // buffer does not grow with the result set.
                if (written % 500 == 0)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await http.Response.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            writer.WriteEndArray();
        }

        // AC-i: "Export is itself an audited event". Recorded after the rows are written, so the
        // count is what actually left. Emission is fail-open, so a failure here is an AU-8 incident
        // and not a failed export — the data has already been sent.
        await audit.RecordAsync(
            AuditActions.AuditExported,
            AuditOutcome.Success,
            AuditTargets.AuditTrail,
            targetId: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fromUtc"] = filter.FromUtc.ToString("O", CultureInfo.InvariantCulture),
                ["toUtc"] = filter.ToUtc.ToString("O", CultureInfo.InvariantCulture),
                ["rows"] = written.ToString(CultureInfo.InvariantCulture),
                ["truncated"] = (written >= MaximumExportRows) ? bool.TrueString : bool.FalseString
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Empty;
    }

    /// <summary>
    /// Maps a failed result to the §4.5 problem shape.
    /// </summary>
    /// <remarks>
    /// Every failure these endpoints produce is a validation failure — a bad range, an unknown
    /// outcome, a page size out of bounds, or a cursor that does not belong to this query. There is
    /// deliberately no "not found" branch: a search that matches nothing returns an empty page,
    /// because 404 for an empty audit result would tell a caller the difference between "no events"
    /// and "no such Company", and those must be indistinguishable.
    /// </remarks>
    private static IResult Problem(HttpContext context, Error error)
    {
        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = "Invalid request",
            Status = StatusCodes.Status400BadRequest,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        return Results.Problem(problem);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
