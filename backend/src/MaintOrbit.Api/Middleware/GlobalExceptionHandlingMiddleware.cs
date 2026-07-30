using System.Text.Json;
using MaintOrbit.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MaintOrbit.Api.Middleware;

/// <summary>
/// Converts an unhandled exception into the documented error envelope.
/// </summary>
/// <remarks>
/// The boundary EX-4 refers to. <c>catch (Exception)</c> is forbidden everywhere else
/// precisely so that it can exist in exactly one place, where the alternative is an
/// unformatted framework response that no client can parse.
/// <para>
/// Every exception becomes <c>internal_error</c> at this milestone. That is the documented
/// category for a platform fault (api-specification §6.2), and it is retryable with backoff
/// (§6.3). Domain and validation failures map to their own categories when the modules that
/// raise them exist — mapping them now would mean inventing the translation before there is
/// anything to translate.
/// </para>
/// </remarks>
internal sealed partial class GlobalExceptionHandlingMiddleware(
    RequestDelegate next,
    ICorrelationIdAccessor accessor,
    ILogger<GlobalExceptionHandlingMiddleware> logger)
{
    /// <summary>Documented category for a platform fault (api-specification §6.2).</summary>
    private const string InternalErrorType = "internal_error";

    /// <summary>
    /// Serialization matching the documented contract: camelCase field names (§1.6), and no
    /// nulls on the wire so a client cannot mistake an absent field for a meaningful one.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Logged once, here. The exception is passed as the exception argument rather than
            // interpolated into the message, so the stack trace stays a structured field
            // (LG-1). The correlation identifier arrives from the ambient logging scope
            // established upstream — it is not repeated into the message.
            UnhandledException(logger, context.Request.Method, context.Request.Path.Value ?? "/", exception);

            if (context.Response.HasStarted)
            {
                // The status line and some body are already on the wire and cannot be
                // retracted. Aborting truncates the response so the client sees a failure;
                // returning normally would hand back a partial body that looks successful.
                // Deliberately not rethrown — the host would log the same exception a second
                // time, and "log once" is the requirement.
                context.Abort();
                return;
            }

            await WriteProblemDetailsAsync(context).ConfigureAwait(false);
        }
    }

    private async Task WriteProblemDetailsAsync(HttpContext context)
    {
        // Clears anything a partially-executed handler set. The correlation header is
        // re-applied by its own OnStarting callback, so it survives this.
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = InternalErrorType,
            Title = "Internal error",
            Status = StatusCodes.Status500InternalServerError,

            // No exception message, no stack trace, no type name. An exception message is
            // assembled from whatever was in scope when it was thrown, which is exactly the
            // material EX-10 and NFR-OBS-009 keep away from anywhere a caller can read. The
            // correlation identifier is what connects this response to the full detail in the
            // logs, which is why it is returned rather than the detail itself.
            Detail = "An unexpected error occurred while processing the request. " +
                     "Retry with backoff. If it persists, contact support and quote the " +
                     "correlation identifier."
        };

        // §4.3 names both of these as fields of the error envelope. They are RFC 7807
        // extension members, which serialize flat alongside type/title/status/detail.
        problem.Extensions["correlationId"] = accessor.Current;
        problem.Extensions["retryable"] = true;

        await JsonSerializer
            .SerializeAsync(context.Response.Body, problem, SerializerOptions, context.RequestAborted)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Error,
        Message = "Unhandled exception processing HTTP {Method} {Path}")]
    private static partial void UnhandledException(
        ILogger logger,
        string method,
        string path,
        Exception exception);
}
