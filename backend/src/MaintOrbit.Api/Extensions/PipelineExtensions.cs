using MaintOrbit.Api.Configuration;
using MaintOrbit.Api.Middleware;
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
    /// <item><description>
    /// <b>Routing, then authentication, then tenant context, then authorization.</b> Routing first
    /// so an endpoint's own requirements are known by the time the request is authenticated.
    /// Tenant context after authentication because it reads the validated principal — and
    /// <b>before</b> authorization because permission resolution is a database read of
    /// <c>employee_roles</c>, which row-level security shows nothing without a Company in scope.
    /// With the two the other way round every permission check resolves an empty set and denies,
    /// which is safe and completely silent: authorization would look implemented and refuse
    /// everybody.
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

        // Routing must precede authentication so an endpoint's own requirements are known by the
        // time the request is authenticated.
        app.UseRouting();

        app.UseAuthentication();

        // Between authentication and authorization, and both halves of that matter. It reads the
        // validated principal, so it cannot run earlier; and it opens the tenant scope that
        // permission resolution reads under, so it cannot run later. It also confirms the session
        // (§3.7), which is what makes revocation effective inside a token's lifetime — an
        // authorization decision made for a revoked session would be a decision made for nobody.
        app.UseMiddleware<TenantContextMiddleware>();

        app.UseAuthorization();

        return app;
    }
}
