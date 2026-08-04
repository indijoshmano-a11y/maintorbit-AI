using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Application.Modules.Identity.Commands.EmployeeRoles;

/// <summary>
/// Assigns a role to an Employee (FR-PERM-003).
/// </summary>
/// <remarks>
/// <b>Validation happens before the aggregate is asked to exist.</b> <c>EmployeeRole.Assign</c>
/// throws for a scope and target that disagree, which is right for an invariant and wrong for a
/// request — a caller who sent a Team scope without a Team made a mistake, not an exceptional one.
/// Each of those conditions is checked here and returned as a result (EX-1).
/// <para>
/// The Employee must exist and be visible. Row-level security makes "belongs to another Company"
/// and "does not exist" the same observation, and §6.2 makes them the same answer — a caller must
/// not be able to probe another tenant's Employee identifiers by watching which ones are refused
/// differently.
/// </para>
/// </remarks>
public sealed partial class AssignRoleCommandHandler(
    ICurrentIdentity currentIdentity,
    IEmployeeRepository employees,
    IAuthorizationRepository authorization,
    IPermissionCache cache,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AssignRoleCommandHandler> logger)
    : ICommandHandler<AssignRoleCommand, AssignedRole>
{
    public async Task<Result<AssignedRole>> HandleAsync(
        AssignRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!RoleCode.TryCreate(command.RoleCode, out var roleCode))
        {
            return Result.Failure<AssignedRole>(
                Error.Validation("A role code is required."));
        }

        if (!Enum.TryParse<PermissionScope>(command.Scope, ignoreCase: true, out var scope))
        {
            return Result.Failure<AssignedRole>(
                Error.Validation("The scope must be Company, Team, or Self."));
        }

        // The two halves of §3.5's shape rule. A Team-scoped assignment with no Team reaches
        // nothing; anything else carrying one implies a limit that is not enforced. Both are
        // configuration that reads as one thing and behaves as another.
        if (scope is PermissionScope.Team && command.ScopeId is null)
        {
            return Result.Failure<AssignedRole>(
                Error.Validation("A Team-scoped assignment must name a Team."));
        }

        if (scope is not PermissionScope.Team && command.ScopeId is not null)
        {
            return Result.Failure<AssignedRole>(
                Error.Validation($"A {scope}-scoped assignment must not name a Team."));
        }

        var employeeId = new EmployeeId(command.EmployeeId);

        var employee = await employees.FindAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            return Result.Failure<AssignedRole>(Error.NotFound("No such Employee."));
        }

        // fk_employee_roles_role_definitions_role_code enforces this in the database. Asking
        // first turns a foreign-key violation into a not_found the caller can act on, the same
        // trade the duplicate check below makes.
        if (!await authorization.RoleExistsAsync(roleCode, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<AssignedRole>(Error.NotFound("No such role."));
        }

        // ux_employee_roles_employee_id_role_code_scope enforces this in the database regardless.
        // Asking first turns a unique-violation exception into an ordinary conflict result, and
        // the constraint stays the guarantee under concurrency.
        if (await authorization
                .AssignmentExistsAsync(employeeId, roleCode, scope, command.ScopeId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<AssignedRole>(
                Error.Conflict("The Employee already holds this role at this scope."));
        }

        var now = timeProvider.GetUtcNow();

        var assignment = EmployeeRole.Assign(
            employee.CompanyId,
            employeeId,
            roleCode,
            scope,
            command.ScopeId,
            now,
            // Who granted it. The caller comes from the validated token, never from the request.
            assignedBy: currentIdentity.RequireEmployeeId());

        authorization.Add(assignment);

        // One command, one commit. Nothing reaches the database before this point.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // After the commit, never before. Invalidating first would leave a window in which a
        // concurrent request repopulates the cache from the pre-change state — which is the one
        // ordering that produces a stale entry with a full lifetime ahead of it.
        await InvalidateAsync(cache, logger, employeeId, employee.CompanyId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new AssignedRole(
            assignment.Id,
            employeeId.Value,
            roleCode,
            scope,
            command.ScopeId,
            assignment.CreatedAtUtc));
    }

    /// <summary>
    /// Drops the Employee's cached permissions, treating a cache fault as an incident.
    /// </summary>
    /// <remarks>
    /// <b>The operation still succeeds.</b> It has committed, and FR-PERM-005 still holds without
    /// the cache: entries expire under sixty seconds, which is the guarantee the requirement
    /// actually asks for — invalidation makes the change immediate rather than making it happen.
    /// Failing the request would report that a committed change did not occur, which is false and
    /// invites a retry that then conflicts with itself.
    /// <para>
    /// It is logged at error, not swallowed. ADR-0021's rule is that fail-open does not mean
    /// unnoticed — a degraded control is an incident even when the request survives it.
    /// </para>
    /// <para>
    /// Shared by both handlers because the reasoning is identical, and the direction it chooses is
    /// the kind that drifts if written twice.
    /// </para>
    /// </remarks>
    internal static async Task InvalidateAsync(
        IPermissionCache cache,
        ILogger logger,
        EmployeeId employeeId,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.InvalidateAsync(employeeId, companyId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            CacheNotInvalidated(logger, employeeId.ToString(), error);
        }
    }

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Error,
        Message = "Role assignments changed for Employee {EmployeeId} but the permission cache " +
                  "could not be invalidated. The change is committed and takes effect when the " +
                  "cached entry expires, within the FR-PERM-005 bound.")]
    private static partial void CacheNotInvalidated(
        ILogger logger, string employeeId, Exception error);
}

/// <summary>
/// Removes a role assignment (FR-PERM-003).
/// </summary>
/// <remarks>
/// <b>The Employee is part of the request as well as the assignment identifier, and is checked.</b>
/// Without it, a caller holding an identifier could remove an assignment through any Employee's
/// URL, and the endpoint's path would describe something other than what happened.
/// <para>
/// This is the direction where a stale cache is unsafe: an assignment that has been removed but is
/// still cached is a permission still in force after it was taken away. That is why invalidation
/// is not optional here — and why the entry lifetime is validated below sixty seconds, so even a
/// failed invalidation is bounded.
/// </para>
/// </remarks>
public sealed class RemoveRoleCommandHandler(
    IAuthorizationRepository authorization,
    IPermissionCache cache,
    IUnitOfWork unitOfWork,
    ILogger<RemoveRoleCommandHandler> logger)
    : ICommandHandler<RemoveRoleCommand>
{
    public async Task<Result> HandleAsync(
        RemoveRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var assignment = await authorization
            .FindAssignmentAsync(command.AssignmentId, cancellationToken).ConfigureAwait(false);

        // Absent, or belonging to another Company — row-level security makes those the same
        // observation, and §6.2 makes them the same answer.
        if (assignment is null)
        {
            return Result.Failure(Error.NotFound("No such role assignment."));
        }

        if (assignment.EmployeeId.Value != command.EmployeeId)
        {
            // Real, visible, and not this Employee's. Answered as not-found rather than as a
            // mismatch, because saying "that assignment exists but belongs to someone else" hands
            // back a fact the caller had not established.
            return Result.Failure(Error.NotFound("No such role assignment."));
        }

        authorization.Remove(assignment);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AssignRoleCommandHandler
            .InvalidateAsync(cache, logger, assignment.EmployeeId, assignment.CompanyId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }
}
