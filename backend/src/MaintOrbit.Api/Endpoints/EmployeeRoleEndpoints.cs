using System.ComponentModel.DataAnnotations;
using MaintOrbit.Api.Authorization;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.Modules.Identity.Commands.EmployeeRoles;
using MaintOrbit.Application.Modules.Identity.Queries.EmployeeRoles;
using MaintOrbit.Domain.Common.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// Role assignment — <c>/api/v1/employees/{employeeId}/roles</c>.
/// </summary>
/// <remarks>
/// api-specification §3.2 lists "role assignment" among the Employees group's resources and
/// "assign role" among its operations, so this is a sub-resource of an Employee rather than a
/// group of its own. That shape is what lets the URL carry the Employee and the permission carry
/// the scope.
/// <para>
/// <b>Reading and changing are different permissions.</b> Listing what somebody holds is
/// <c>employee.read [C]</c>; changing it is <c>employee.manage [C]</c>, because deciding what
/// another Employee may do is strictly larger than seeing what they may do now — and an
/// administrator who can grant roles can grant themselves anything the catalogue offers.
/// </para>
/// <para>
/// <b>No role is named anywhere here.</b> SD-020 makes roles presets; an endpoint that special-cased
/// one would be the role conditional CLAUDE.md §9 forbids, and an architecture test refuses it.
/// </para>
/// </remarks>
public static class EmployeeRoleEndpoints
{
    /// <summary>Maps the role assignment endpoints.</summary>
    public static IEndpointRouteBuilder MapEmployeeRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var basePath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ApiOptions>>().Value.BasePath;

        var group = endpoints.MapGroup($"{basePath}/employees/{{employeeId:guid}}/roles");

        group.MapGet("/", ListAsync)
            .RequirePermission(IdentityPermissions.EmployeeRead);

        group.MapPost("/", AssignAsync)
            .RequirePermission(IdentityPermissions.EmployeeManage);

        group.MapDelete("/{assignmentId:guid}", RemoveAsync)
            .RequirePermission(IdentityPermissions.EmployeeManage);

        return endpoints;
    }

    /// <summary>Lists what one Employee holds.</summary>
    private static async Task<IResult> ListAsync(
        Guid employeeId,
        HttpContext context,
        IQueryHandler<ListEmployeeRolesQuery, EmployeeRoleList> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new ListEmployeeRolesQuery(employeeId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new EmployeeRoleCollectionResponse(
                [.. result.Value.Items.Select(Map)], result.Value.TotalCount))
            : Problem(context, result.Error);
    }

    /// <summary>
    /// Grants a role.
    /// </summary>
    /// <remarks>
    /// <c>201</c> with a <c>Location</c>, per §4.1's table for a create. The location addresses the
    /// assignment, which is what removal takes — an Employee may hold one role over two Teams, so
    /// the role code is not a handle.
    /// </remarks>
    private static async Task<IResult> AssignAsync(
        Guid employeeId,
        AssignRoleRequest request,
        HttpContext context,
        ICommandHandler<AssignRoleCommand, AssignedRole> handler,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(
            new AssignRoleCommand(employeeId, request.RoleCode, request.Scope, request.ScopeId),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Problem(context, result.Error);
        }

        var basePath = options.Value.BasePath;

        return Results.Created(
            $"{basePath}/employees/{employeeId}/roles/{result.Value.Id}", Map(result.Value));
    }

    /// <summary>Takes a role away.</summary>
    /// <remarks><c>204</c>, per §4.1's table for a delete.</remarks>
    private static async Task<IResult> RemoveAsync(
        Guid employeeId,
        Guid assignmentId,
        HttpContext context,
        ICommandHandler<RemoveRoleCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new RemoveRoleCommand(employeeId, assignmentId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.NoContent() : Problem(context, result.Error);
    }

    private static EmployeeRoleResponse Map(EmployeeRoleAssignment assignment) =>
        new(assignment.Id,
            assignment.RoleCode.Value,
            assignment.Scope.ToString(),
            assignment.ScopeId,
            assignment.AssignedAtUtc);

    private static EmployeeRoleResponse Map(AssignedRole assignment) =>
        new(assignment.Id,
            assignment.RoleCode.Value,
            assignment.Scope.ToString(),
            assignment.ScopeId,
            assignment.AssignedAtUtc);

    /// <summary>Runs DataAnnotations and returns the documented validation envelope (§4.5).</summary>
    private static IResult? Validate<TRequest>(TRequest request)
        where TRequest : notnull
    {
        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(request, context, results, validateAllProperties: true))
        {
            return null;
        }

        var problem = new ProblemDetails
        {
            Type = "validation_failed",
            Title = "The request is not valid",
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more fields are missing or malformed."
        };

        problem.Extensions["errors"] = results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty),
                (result, member) => new
                {
                    field = ToCamelCase(member),
                    code = "required",
                    message = result.ErrorMessage
                })
            .ToArray();

        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Writes the documented error envelope (§4.3).
    /// </summary>
    /// <remarks>
    /// §7's table: <c>validation_failed</c> is 400, <c>not_found</c> is 404 — which §7 also uses
    /// for a cross-tenant reference, "never 403", because a forbidden answer confirms the thing
    /// exists — and <c>conflict</c> is 409.
    /// </remarks>
    private static IResult Problem(HttpContext context, Error error)
    {
        var (status, title) = error.Code switch
        {
            "validation_failed" => (StatusCodes.Status400BadRequest, "The request is not valid"),
            "conflict" => (StatusCodes.Status409Conflict, "The request conflicts with the current state"),
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

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>A request to grant a role.</summary>
/// <remarks>
/// There is no Employee field: the URL carries it. A body that could name a different one would
/// make the path describe something other than what happened.
/// </remarks>
public sealed record AssignRoleRequest
{
    /// <summary>The role, as defined in the catalogue.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string RoleCode { get; init; } = string.Empty;

    /// <summary>How far it reaches — Company, Team, or Self (§3.5).</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(16, MinimumLength = 1)]
    public string Scope { get; init; } = string.Empty;

    /// <summary>The Team, for a Team-scoped assignment.</summary>
    /// <remarks>
    /// Whether it is required depends on the scope, which DataAnnotations cannot express across
    /// two fields. The handler decides, so the rule lives with the rest of §3.5's shape rule
    /// rather than half here and half there.
    /// </remarks>
    public Guid? ScopeId { get; init; }
}

/// <summary>A role assignment as the API returns it.</summary>
public sealed record EmployeeRoleResponse(
    Guid Id, string RoleCode, string Scope, Guid? ScopeId, DateTimeOffset AssignedAtUtc);

/// <summary>An Employee's assignments.</summary>
public sealed record EmployeeRoleCollectionResponse(
    IReadOnlyList<EmployeeRoleResponse> Items, int TotalCount);
