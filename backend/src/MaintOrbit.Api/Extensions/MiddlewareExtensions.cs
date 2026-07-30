using MaintOrbit.Api.Middleware;

namespace MaintOrbit.Api.Extensions;

/// <summary>
/// Registers individual middleware components.
/// </summary>
/// <remarks>
/// Each method adds exactly one component and says nothing about where it belongs. Ordering
/// is decided in one place — <see cref="PipelineExtensions.UseApiPipeline"/> — because AC-8
/// makes middleware order a correctness property, and a correctness property spread across
/// call sites is one nobody owns.
/// </remarks>
public static class MiddlewareExtensions
{
    /// <summary>
    /// Resolves the correlation identifier and returns it on the response.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CorrelationIdMiddleware>();
    }

    /// <summary>
    /// Emits one structured log entry per request.
    /// </summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<RequestLoggingMiddleware>();
    }

    /// <summary>
    /// Converts unhandled exceptions into the documented error envelope.
    /// </summary>
    public static IApplicationBuilder UseExceptionHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
    }
}
