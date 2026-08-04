using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Modules.Identity.Queries.EmployeeRoles;

/// <summary>
/// The roles one Employee holds (§3.2).
/// </summary>
/// <remarks>
/// No Company parameter: TC-1 derives the tenant from the credential, and §5.1 states it for
/// filters — "Tenant: never a filter". Row-level security decides which assignments exist for this
/// request.
/// </remarks>
/// <param name="EmployeeId">Whose roles to list.</param>
public sealed record ListEmployeeRolesQuery(Guid EmployeeId) : IQuery<EmployeeRoleList>;

/// <summary>One role assignment, as the API returns it.</summary>
/// <remarks>
/// The identifier is the handle removal uses. An Employee may hold the same role over two Teams,
/// so the (role, scope) pair is not something a client can address.
/// </remarks>
public sealed record EmployeeRoleAssignment(
    Guid Id,
    RoleCode RoleCode,
    PermissionScope Scope,
    Guid? ScopeId,
    DateTimeOffset AssignedAtUtc);

/// <summary>An Employee's assignments.</summary>
/// <remarks>
/// Unpaged, and that is a bounded claim rather than an oversight. §4.4 carries a total on small
/// bounded collections because counting them is cheap; the number of roles one Employee holds is
/// bounded by the size of the role catalogue times the Teams they belong to, which is not a page.
/// </remarks>
public sealed record EmployeeRoleList(IReadOnlyList<EmployeeRoleAssignment> Items, int TotalCount);

/// <summary>Reads an Employee's role assignments.</summary>
/// <remarks>
/// The Employee is confirmed to exist first. Without that, listing the roles of an identifier
/// nobody holds returns an empty list — indistinguishable from an Employee who holds none, which
/// tells a caller that an unknown identifier is a real Employee with no roles.
/// </remarks>
public sealed class ListEmployeeRolesQueryHandler(
    IEmployeeRepository employees,
    IAuthorizationRepository authorization)
    : IQueryHandler<ListEmployeeRolesQuery, EmployeeRoleList>
{
    public async Task<Result<EmployeeRoleList>> HandleAsync(
        ListEmployeeRolesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var employeeId = new EmployeeId(query.EmployeeId);

        var employee = await employees.FindAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            return Result.Failure<EmployeeRoleList>(Error.NotFound("No such Employee."));
        }

        var assignments = await authorization
            .FindRolesForAsync(employeeId, cancellationToken).ConfigureAwait(false);

        var items = assignments
            // Deterministic, so two identical requests do not disagree about order (§5.2). The
            // identifier is UUIDv7, so this is also assignment order.
            .OrderBy(assignment => assignment.Id)
            .Select(assignment => new EmployeeRoleAssignment(
                assignment.Id,
                assignment.RoleCode,
                assignment.ScopeType,
                assignment.ScopeId,
                assignment.CreatedAtUtc))
            .ToList();

        return Result.Success(new EmployeeRoleList(items, items.Count));
    }
}
