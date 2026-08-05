using System.Text.Json;
using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Shared.Auditing;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Infrastructure.Auditing;

/// <summary>
/// Writes audit events to the structured log, because the append-only store is not built.
/// </summary>
/// <remarks>
/// <b>Named for what it is.</b> This is not the audit trail §3.3 describes and must not be mistaken
/// for it: that is a durable stream drained by a batch writer into
/// <c>auditing.audit_events</c> — append-only, partitioned by month, searchable within thirty
/// seconds (AU-9), retained per policy (AU-7), reconciled against stream offsets (AU-8). A log
/// line satisfies none of those.
/// <para>
/// <b>What it does satisfy is the part identity owns.</b> Every documented authentication and
/// authorization event is emitted, with the actor, action, target, outcome, time, and correlation
/// AU-3 requires — so the recording side is one adapter, not a search through every handler for
/// the places somebody forgot.
/// </para>
/// <para>
/// The <c>auditing</c> module owns the store, and identity may not create or write another
/// module's tables (ADR-0002, CLAUDE.md §7). That module does not exist, and neither does the
/// ADR-0013 outbox or the worker that would drain the stream.
/// </para>
/// <para>
/// Logged at <see cref="LogLevel.Information"/> — an audit event is a record of ordinary business,
/// not a fault. The failure to <i>write</i> one is the error, and that is the trail's to report.
/// </para>
/// </remarks>
internal sealed partial class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : IAuditSink
{
    /// <summary>Compact and stable, so a log scrape can parse what a reader can read.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1873:Avoid potentially expensive logging",
        Justification =
            "The rule guards against formatting work done when the level is off. It cannot be " +
            "done here: the method throws when Information is disabled rather than returning, " +
            "because an audit record dropped by a log-level change would be sampling by another " +
            "name (AU-2). The arguments are therefore never evaluated while logging is off, and " +
            "the serialization is the payload rather than incidental formatting.")]
    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (!logger.IsEnabled(LogLevel.Information))
        {
            // A log level is configuration; an audit record is a compliance obligation. Turning
            // Information off must not quietly stop the trail — AU-2 forbids sampling "under any
            // load condition", and a level filter is sampling by another name. Throwing makes the
            // trail record an AU-8 incident at Error, which is a different level and normally on.
            //
            // It is also the sharpest illustration of why this sink is a placeholder: no
            // append-only store would have this failure mode.
            throw new InvalidOperationException(
                "Audit events cannot be recorded while Information logging is disabled.");
        }

        Recorded(
            logger,
            auditEvent.Action,
            auditEvent.Outcome.ToString(),
            auditEvent.ActorEmployeeId?.ToString("n") ?? "anonymous",
            auditEvent.CompanyId?.ToString("n") ?? "none",
            JsonSerializer.Serialize(auditEvent, Json));

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1601,
        Level = LogLevel.Information,
        Message = "AUDIT {Action} {Outcome} actor={ActorEmployeeId} company={CompanyId} {Event}",
        SkipEnabledCheck = true)]
    private static partial void Recorded(
        ILogger logger,
        string action,
        string outcome,
        string actorEmployeeId,
        string companyId,
        string @event);
}
