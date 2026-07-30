using MaintOrbit.Api.Configuration;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Extensions;

/// <summary>
/// The canonical middleware order.
/// </summary>
/// <remarks>
/// AC-8 states that middleware order is fixed and that changing it is a correctness change.
/// The order lives here as a single sequence rather than as a list of calls in
/// <c>Program.cs</c>, so that changing it is a visible edit to a documented decision instead
/// of a line moved during an unrelated change.
/// </remarks>
public static class PipelineExtensions
{
    /// <summary>
    /// Applies the request pipeline in its fixed order.
    /// </summary>
    /// <remarks>
    /// Each position is load-bearing:
    /// <list type="number">
    /// <item><description>
    /// <b>Forwarded headers</b> first, because everything after it reads the scheme and the
    /// client address. A component that runs before this sees the proxy instead of the caller,
    /// and would be wrong without any indication that it was.
    /// </description></item>
    /// <item><description>
    /// <b>Correlation</b> next, so that every subsequent component — including the exception
    /// handler, whose response is the one a caller most needs to quote — runs inside the
    /// logging scope and can reach the identifier.
    /// </description></item>
    /// <item><description>
    /// <b>Request logging</b> outside the exception handler, so a failed request is recorded
    /// with the status the caller actually received rather than the status in effect when the
    /// exception was thrown.
    /// </description></item>
    /// <item><description>
    /// <b>Exception handling</b> innermost of the cross-cutting three, wrapping routing,
    /// endpoints, and everything later milestones add between here and the handler.
    /// </description></item>
    /// </list>
    /// Endpoint mapping stays with the composition root, since what is mapped is a
    /// composition decision rather than an ordering one.
    /// </remarks>
    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var reverseProxy = app.ApplicationServices
            .GetRequiredService<IOptions<ReverseProxyOptions>>().Value;

        if (reverseProxy.Enabled)
        {
            // Only when a trusted proxy is named. Processing X-Forwarded-* on a host that is
            // reachable directly would let any caller assert its own address.
            app.UseForwardedHeaders();
        }

        app.UseCorrelationId();
        app.UseRequestLogging();
        app.UseExceptionHandling();

        return app;
    }
}
