using MaintOrbit.Api.Authorization;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.Modules.Identity.Queries.Employees;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// The Employees endpoints — <c>/api/v1/employees</c>.
/// </summary>
/// <remarks>
/// The first endpoints in the system that a permission decides. Everything before this milestone
/// was either unauthenticated by necessity — sign-in, refresh, password reset — or gated on
/// nothing more than holding a session.
/// <para>
/// api-specification §3.2 names this group's permissions as <c>employee.read [C]</c>,
/// <c>employee.invite [C]</c>, and <c>employee.manage [C]</c>, with <c>employee.read [S]</c> for
/// <c>/me</c>. <b>Two of the group's nine documented operations are built here</b> — the two reads
/// — because the milestone is the enforcement path, not the Employee surface. Each remaining
/// operation arrives with the use case behind it, declaring its own permission the same way.
/// </para>
/// <para>
/// Every endpoint is a thin translation: bind, call one handler, map the result. No endpoint
/// checks a permission itself; the declaration is the whole of what it says about authorization,
/// and the pipeline decides before the handler runs.
/// </para>
/// </remarks>
public static class EmployeeEndpoints
{
    /// <summary>Maps the Employees endpoints.</summary>
    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var basePath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ApiOptions>>().Value.BasePath;

        var group = endpoints.MapGroup($"{basePath}/employees");

        // The directory. Company scope, because reading other people's records is a Company-wide
        // capability — §3.2 gives it as employee.read [C].
        group.MapGet("/", ListAsync)
            .RequirePermission(IdentityPermissions.EmployeeRead);

        // Mapped before "/" would ever be considered for it, because a literal segment beats a
        // parameter — but there is no parameterised route here yet, and when one arrives this
        // ordering is what keeps /me from being read as an identifier.
        //
        // Self scope. §3.5's rule is that a Company grant reaches everything and a Self grant
        // reaches only the acting Employee, so an Employee who can read nobody else can still
        // read themselves.
        group.MapGet("/me", GetCurrentAsync)
            .RequirePermission(IdentityPermissions.EmployeeRead, PermissionScope.Self);

        return endpoints;
    }

    /// <summary>Returns a page of the Company's Employees.</summary>
    /// <remarks>
    /// <b>No Company parameter, and none is possible.</b> §5.1: "Tenant: never a filter — always
    /// derived from the credential". The rows are whatever row-level security shows for the scope
    /// the pipeline opened, which is the Company in the validated token.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        HttpContext context,
        IQueryHandler<ListEmployeesQuery, EmployeePage> handler,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var limits = options.Value;

        // Clamped rather than rejected. §5.5 bounds page size; a caller asking for more than the
        // maximum is asking for a page the API will not serve, and refusing outright would make a
        // client's optimism an error rather than a ceiling.
        var resolvedSize = Math.Clamp(
            pageSize ?? limits.DefaultPageSize, 1, limits.MaxPageSize);

        var resolvedPage = Math.Max(page ?? 1, 1);

        var result = await handler
            .HandleAsync(new ListEmployeesQuery(resolvedPage, resolvedSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new EmployeeCollectionResponse(
                [.. result.Value.Items.Select(Map)],
                result.Value.Page,
                result.Value.PageSize,
                result.Value.TotalCount,
                result.Value.HasMore))
            : Problem(context, result.Error);
    }

    /// <summary>Returns the caller's own record.</summary>
    private static async Task<IResult> GetCurrentAsync(
        HttpContext context,
        IQueryHandler<GetCurrentEmployeeQuery, EmployeeSummary> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new GetCurrentEmployeeQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.Ok(Map(result.Value)) : Problem(context, result.Error);
    }

    private static EmployeeResponse Map(EmployeeSummary employee) =>
        new(employee.Id,
            employee.Email,
            employee.Status.ToString(),
            employee.EmailVerifiedAtUtc,
            employee.CreatedAtUtc);

    /// <summary>
    /// Writes the documented error envelope (§4.3).
    /// </summary>
    /// <remarks>
    /// <c>not_found</c> is the only code these reads produce. §7 is explicit that it also covers a
    /// resource in another Company — "cross-tenant references return 404, never 403" — because a
    /// forbidden answer confirms the thing exists.
    /// </remarks>
    private static IResult Problem(HttpContext context, Error error)
    {
        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = "Not found",
            Status = StatusCodes.Status404NotFound,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: StatusCodes.Status404NotFound);
    }
}

/// <summary>An Employee as the API returns them.</summary>
/// <remarks>
/// C2 fields only, and the omissions are the point: no credential, no session, no MFA state. Those
/// live in separate aggregates so that a directory read cannot carry them, and a response shape
/// that included them would undo that at the last step.
/// </remarks>
public sealed record EmployeeResponse(
    string Id,
    string Email,
    string Status,
    DateTimeOffset? EmailVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>A page of Employees, in the documented collection envelope (§4.4).</summary>
public sealed record EmployeeCollectionResponse(
    IReadOnlyList<EmployeeResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);
