using System.Text.Json;
using MaintOrbit.Api.Authentication;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Mvc;

namespace MaintOrbit.Api.Middleware;

/// <summary>
/// Establishes the tenant context and confirms the session, for an authenticated request.
/// </summary>
/// <remarks>
/// <b>TC-1 in one place.</b> The Company is taken from the validated token and from nowhere else —
/// never a header, a query parameter, or a body field. The rule most likely to be broken by a
/// well-meaning convenience feature is exactly this one, and a "switch company" parameter would
/// reintroduce client-controlled tenancy.
/// <para>
/// The session is confirmed <b>after</b> the tenant scope opens, and it has to be: row-level
/// security means a session lookup without a Company in scope finds nothing. The token's Company
/// claim is what makes the lookup possible, and the lookup is what confirms the token's claim is
/// still true.
/// </para>
/// <para>
/// The scope wraps the rest of the pipeline and is disposed on the way out (TC-2, TC-4) — so a
/// connection checked out downstream carries the tenant, and one checked out after the response
/// does not.
/// </para>
/// </remarks>
internal sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ISessionValidator sessionValidator)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Health probes and, for now, everything else. An unauthenticated request runs with no
            // tenant, which means row-level security shows it nothing — the documented failure
            // direction rather than an error.
            await next(context).ConfigureAwait(false);
            return;
        }

        var employeeId = context.User.GetEmployeeId();
        var companyId = context.User.GetCompanyId();
        var sessionId = context.User.GetSessionId();

        if (employeeId is null || companyId is null || sessionId is null)
        {
            // The token validated but is missing an identity claim, so it was signed by a trusted
            // key and is still unusable. Refusing is the only safe reading: a request with no
            // Company cannot be given a tenant context, and running it without one would silently
            // return empty results instead of failing.
            await RejectAsync(context, Error.AuthenticationFailed("The access token is incomplete."))
                .ConfigureAwait(false);
            return;
        }

        using var scope = tenantContext.BeginTenantScope(companyId.Value);

        var session = await sessionValidator
            .ValidateAsync(sessionId.Value, employeeId.Value, companyId.Value, context.RequestAborted)
            .ConfigureAwait(false);

        if (session.IsFailure)
        {
            // A signed token proves what was true when it was issued; it cannot prove the session
            // still exists. This is what makes revocation mean anything within a token's lifetime.
            await RejectAsync(context, session.Error).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the documented error envelope for a rejected authenticated request.
    /// </summary>
    /// <remarks>
    /// Shaped like every other error the API returns (§4.3), and carries the correlation
    /// identifier so a support conversation about "I was signed out" has something to start from.
    /// </remarks>
    private static async Task RejectAsync(HttpContext context, Error error)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.WWWAuthenticate = "Bearer";

        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = "Authentication failed",
            Status = StatusCodes.Status401Unauthorized,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                problem,
                Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
