namespace MaintOrbit.Api.Middleware;

/// <summary>
/// Emits one structured log entry per request.
/// </summary>
/// <remarks>
/// One entry, written after the response is settled, carrying method, path, status, and
/// duration. A started/completed pair would double log volume to add nothing — the start of a
/// request is implied by its completion, and a request that never completes is visible as an
/// absence rather than needing its own line.
/// <para>
/// Sits <i>outside</i> the exception handler in the pipeline, so a request that failed is
/// still logged with the status the caller actually received rather than the status it had
/// when the exception was thrown.
/// </para>
/// </remarks>
internal sealed partial class RequestLoggingMiddleware(
    RequestDelegate next,
    TimeProvider timeProvider,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // TimeProvider rather than Stopwatch: U-3 forbids reaching for the ambient clock, and
        // AT-9 enforces it. GetTimestamp is monotonic, so the duration is unaffected by a
        // wall-clock adjustment mid-request.
        var start = timeProvider.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var level = LevelFor(context.Response.StatusCode);

            // Guarded so that nothing is computed when the level is disabled. This runs on
            // every request, including the Gateway's 15 ms budget, so the work of formatting a
            // path and a duration is not worth paying for output that is discarded.
            if (logger.IsEnabled(level))
            {
                var elapsedMilliseconds = timeProvider.GetElapsedTime(start).TotalMilliseconds;

                RequestCompleted(
                    logger,
                    level,
                    context.Request.Method,
                    // Path only. A query string is caller-controlled and is a routine place for
                    // a token or an identifier to end up, and LG-2 does not bend for
                    // convenience.
                    context.Request.Path.Value ?? "/",
                    context.Response.StatusCode,
                    elapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Maps a status code onto a severity, per LG-6.
    /// </summary>
    /// <remarks>
    /// LG-6 defines <c>Error</c> as requiring action and <c>Warning</c> as a degraded
    /// condition. A 4xx is the caller's mistake and needs nobody woken; a 5xx is ours. Logging
    /// every 404 at <c>Error</c> is the fastest way to teach an on-call engineer to ignore the
    /// error channel.
    /// </remarks>
    private static LogLevel LevelFor(int statusCode) => statusCode switch
    {
        >= 500 => LogLevel.Error,
        >= 400 => LogLevel.Warning,
        _ => LogLevel.Information
    };

    /// <remarks>
    /// Source-generated (CA1848). Every request passes through here, so the allocation and
    /// boxing of a plain <c>ILogger.Log</c> call would be paid on the hot path — including the
    /// Gateway's 15 ms budget — even when the level is disabled.
    /// </remarks>
    [LoggerMessage(
        EventId = 1000,
        Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms")]
    private static partial void RequestCompleted(
        ILogger logger,
        LogLevel level,
        string method,
        string path,
        int statusCode,
        double elapsedMilliseconds);
}
