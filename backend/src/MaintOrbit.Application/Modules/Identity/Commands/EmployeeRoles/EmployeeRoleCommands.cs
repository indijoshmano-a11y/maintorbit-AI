using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Modules.Identity.Commands.EmployeeRoles;

/// <summary>
/// Grants an Employee a role, within a scope (§3.2 — "assign role").
/// </summary>
/// <remarks>
/// The Employee and the role arrive from the request; the Company does not, and cannot. TC-1
/// derives the tenant from the credential, so an assignment is always made within the caller's own
/// Company — which is also what makes row-level security able to refuse an Employee identifier
/// belonging to someone else's.
/// </remarks>
/// <param name="EmployeeId">Who receives the role.</param>
/// <param name="RoleCode">Which role, as defined in <c>role_definitions</c>.</param>
/// <param name="Scope">How far it reaches — Company, Team, or Self (§3.5).</param>
/// <param name="ScopeId">The Team, for a Team-scoped assignment; otherwise absent.</param>
public sealed record AssignRoleCommand(
    Guid EmployeeId,
    string? RoleCode,
    string? Scope,
    Guid? ScopeId) : ICommand<AssignedRole>;

/// <summary>The assignment that was created.</summary>
/// <remarks>
/// Carries its identifier because §4.1 returns <c>201</c> with a <c>Location</c>, and removal
/// addresses the assignment rather than the (Employee, role, scope) triple — an Employee may hold
/// the same role at two different Team scopes, so the triple is not a handle a client can use.
/// </remarks>
public sealed record AssignedRole(
    Guid Id,
    Guid EmployeeId,
    RoleCode RoleCode,
    PermissionScope Scope,
    Guid? ScopeId,
    DateTimeOffset AssignedAtUtc);

/// <summary>
/// Takes a role away from an Employee (§3.2).
/// </summary>
/// <remarks>
/// Addressed by assignment identifier. Naming the role instead would be ambiguous the moment an
/// Employee holds one role over two Teams, and resolving that ambiguity by removing "all of them"
/// is not something a caller should get by accident.
/// </remarks>
/// <param name="EmployeeId">The Employee the assignment must belong to.</param>
/// <param name="AssignmentId">Which assignment.</param>
public sealed record RemoveRoleCommand(Guid EmployeeId, Guid AssignmentId) : ICommand;
