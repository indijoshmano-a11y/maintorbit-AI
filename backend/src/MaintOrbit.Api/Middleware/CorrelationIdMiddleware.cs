using MaintOrbit.Infrastructure.Telemetry.Logging;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.Primitives;

namespace MaintOrbit.Api.Middleware;

/// <summary>
/// Establishes the correlation identifier for the request and returns it to the caller.
/// </summary>
/// <remarks>
/// This is the ingress the API specification refers to (§12.1) and the point at which
/// NFR-OBS-002 is satisfied: the identifier is resolved once, made ambient for every
/// component that runs afterwards, attached to the logging scope so LG-4 holds for entries
/// this middleware never sees, and written back on the response.
/// <para>
/// The resolution rules themselves — generate when absent, reuse when valid, replace when
/// malformed — belong to <see cref="CorrelationId"/> and are not repeated here. This
/// middleware decides only <i>where</i> the candidate comes from.
/// </para>
/// </remarks>
internal sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ICorrelationIdAccessor accessor,
    ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var supplied = context.Request.Headers[CorrelationHeaderNames.CorrelationId].ToString();
        var correlationId = CorrelationId.Resolve(supplied);

        // Registered as a callback rather than assigned now, so the header survives anything
        // downstream that resets the response — the global exception handler clears headers
        // before writing ProblemDetails, and an error response is exactly the one a caller
        // most needs the identifier from.
        context.Response.OnStarting(static state =>
        {
            var (response, id) = ((HttpResponse, string))state;
            response.Headers[CorrelationHeaderNames.CorrelationId] = id;
            return Task.CompletedTask;
        }, (context.Response, correlationId));

        using var ambient = accessor.BeginCorrelationScope(correlationId);
        using var scope = logger.BeginCorrelationScope(correlationId);

        await next(context).ConfigureAwait(false);
    }
}
