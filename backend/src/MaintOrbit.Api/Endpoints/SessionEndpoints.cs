using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.Sessions;
using MaintOrbit.Domain.Common.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// The caller's own sessions — <c>/api/v1/employees/me/sessions</c>.
/// </summary>
/// <remarks>
/// api-specification §3.2 lists "list own sessions" and "revoke session" among the Employees
/// group's operations, and makes <c>/me</c> the path for anything about the caller themselves.
/// FR-AUTH-008 is the requirement: "Employees must be able to view their active sessions and
/// terminate any of them."
/// <para>
/// <b>Authentication only, no permission.</b> These act on the caller's own sessions and nobody
/// else's, which is the same capability <c>/auth/logout</c> already has without one. Requiring a
/// permission would mean an Employee who has spotted a session they do not recognise could be
/// unable to end it because an administrator had not granted something — a security control gated
/// on an administrative act is a control that fails when it is most needed. Terminating
/// <i>another</i> Employee's sessions is FR-AUTH-009, a different capability that is not built
/// here.
/// </para>
/// </remarks>
public static class SessionEndpoints
{
    /// <summary>Maps the session management endpoints.</summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var basePath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ApiOptions>>().Value.BasePath;

        var group = endpoints
            .MapGroup($"{basePath}/employees/me/sessions")
            .RequireAuthorization();

        // Mapped before the parameterised delete would be considered for it: a literal segment
        // beats a route parameter, but stating the order keeps "current" from ever being read as
        // an identifier.
        group.MapGet("/current", GetCurrentAsync);
        group.MapPost("/current/activity", RecordActivityAsync);

        group.MapGet("/", ListAsync);
        group.MapDelete("/", RevokeOthersAsync);
        group.MapDelete("/{sessionId:guid}", RevokeAsync);

        return endpoints;
    }

    /// <summary>Lists the caller's active sessions (FR-AUTH-008).</summary>
    /// <remarks>
    /// A total, no paging. §4.4 carries one on small bounded collections; the number of devices one
    /// Employee is signed in on is bounded by how many they own, which is not a page.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        HttpContext context,
        IQueryHandler<ListSessionsQuery, IReadOnlyList<EmployeeSession>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new ListSessionsQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new SessionCollectionResponse(
                [.. result.Value.Select(Map)], result.Value.Count))
            : Problem(context, result.Error);
    }

    /// <summary>Returns the session this request is authenticated with.</summary>
    private static async Task<IResult> GetCurrentAsync(
        HttpContext context,
        IQueryHandler<GetCurrentSessionQuery, EmployeeSession> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new GetCurrentSessionQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.Ok(Map(result.Value)) : Problem(context, result.Error);
    }

    /// <summary>Ends one session (FR-AUTH-008).</summary>
    /// <remarks><c>204</c>, per §4.1's table for a delete.</remarks>
    private static async Task<IResult> RevokeAsync(
        Guid sessionId,
        HttpContext context,
        ICommandHandler<RevokeSessionCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new RevokeSessionCommand(sessionId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.NoContent() : Problem(context, result.Error);
    }

    /// <summary>
    /// Ends every session except this one.
    /// </summary>
    /// <remarks>
    /// <c>DELETE</c> on the collection, which §4.1 does not tabulate but follows from it — the
    /// collection is the caller's other devices, and the current session is deliberately not part
    /// of what is deleted. It returns how many were ended, because "we signed out 4 devices" is
    /// the confirmation the Employee actually wanted.
    /// </remarks>
    private static async Task<IResult> RevokeOthersAsync(
        HttpContext context,
        ICommandHandler<RevokeOtherSessionsCommand, int> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new RevokeOtherSessionsCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new SessionsRevokedResponse(result.Value))
            : Problem(context, result.Error);
    }

    /// <summary>
    /// Records genuine interaction, resetting the idle window (§3.2, SM-b).
    /// </summary>
    /// <remarks>
    /// <b>Explicit, because the alternative is forbidden.</b> SM-b says the activity signal "must
    /// come from interaction, not from the SignalR connection or automatic refetches" — middleware
    /// touching every request would keep an unattended desk signed in forever. A client calls this
    /// on real interaction and on nothing else.
    /// </remarks>
    private static async Task<IResult> RecordActivityAsync(
        HttpContext context,
        ICommandHandler<RecordSessionActivityCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new RecordSessionActivityCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.NoContent() : Problem(context, result.Error);
    }

    private static SessionResponse Map(EmployeeSession session) =>
        new(session.Id,
            session.ClientType.ToString(),
            session.DeviceLabel,
            session.IpAddress,
            session.CoarseLocation,
            session.CreatedAtUtc,
            session.LastActiveAtUtc,
            session.AbsoluteExpiresAtUtc,
            session.IsCurrent);

    /// <summary>
    /// Writes the documented error envelope (§4.3).
    /// </summary>
    /// <remarks>
    /// <c>not_found</c> and <c>authentication_failed</c> are the only codes these produce — the
    /// first for a session that is absent or is not the caller's, which §7 requires be
    /// indistinguishable, and the second for activity on a session that has since ended.
    /// </remarks>
    private static IResult Problem(HttpContext context, Error error)
    {
        var (status, title) = error.Code switch
        {
            "authentication_failed" => (StatusCodes.Status401Unauthorized, "Authentication failed"),
            _ => (StatusCodes.Status404NotFound, "Not found")
        };

        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = title,
            Status = status,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: status);
    }
}

/// <summary>
/// A session as the Employee's device list shows it.
/// </summary>
/// <remarks>
/// The address and location are included on purpose: §4.2 classifies them as personal data about
/// the Employee and states they are "visible to the Employee (principle P-7)". A device list that
/// hid where a session was opened from could not answer the question it exists for.
/// <para>
/// No token, no refresh chain, no session secret — nothing here can be used to act as the session.
/// </para>
/// </remarks>
public sealed record SessionResponse(
    string Id,
    string ClientType,
    string? DeviceLabel,
    string? IpAddress,
    string? CoarseLocation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActiveAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    bool IsCurrent);

/// <summary>The caller's active sessions.</summary>
public sealed record SessionCollectionResponse(
    IReadOnlyList<SessionResponse> Items, int TotalCount);

/// <summary>How many sessions were ended.</summary>
public sealed record SessionsRevokedResponse(int RevokedCount);
