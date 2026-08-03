using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A role held by an Employee, within a scope.
/// </summary>
/// <remarks>
/// The tenant-scoped half of authorization, and the only one of the four tables that carries a
/// <c>company_id</c> — the catalogue, the role definitions, and their grants are platform-wide;
/// who holds what is per Company.
/// <para>
/// §4.2 gives this row <c>scope_type</c> and <c>scope_id</c>. An Employee may hold several: §3.4
/// notes multiple roles are permitted, and their permissions union — there is no hierarchy to
/// resolve, so holding two roles simply grants both sets.
/// </para>
/// </remarks>
public sealed class EmployeeRole
{
    private EmployeeRole()
    {
    }

    private EmployeeRole(
        Guid id,
        CompanyId companyId,
        EmployeeId employeeId,
        RoleCode roleCode,
        PermissionScope scopeType,
        Guid? scopeId,
        DateTimeOffset assignedAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        RoleCode = roleCode;
        ScopeType = scopeType;
        ScopeId = scopeId;
        CreatedAtUtc = assignedAtUtc;
        UpdatedAtUtc = assignedAtUtc;
    }

    /// <summary>Identifier.</summary>
    public Guid Id { get; private init; }

    /// <summary>The Company — the tenant discriminator (DB-P1).</summary>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee holding the role.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>The role held.</summary>
    public RoleCode RoleCode { get; private init; }

    /// <summary>How far the assignment reaches.</summary>
    public PermissionScope ScopeType { get; private init; }

    /// <summary>
    /// What it is scoped to — a Team for <see cref="PermissionScope.Team"/>, otherwise null.
    /// </summary>
    /// <remarks>
    /// Carried as a bare identifier: Teams belong to the tenancy module, and DB-P2 forbids a
    /// foreign key across module schemas.
    /// </remarks>
    public Guid? ScopeId { get; private init; }

    /// <summary>When the role was assigned (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>Who assigned it.</summary>
    public EmployeeId? CreatedByEmployeeId { get; private set; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Assigns a role to an Employee.</summary>
    /// <exception cref="ArgumentException">
    /// An identifier is unset, or the scope and its target disagree.
    /// </exception>
    public static EmployeeRole Assign(
        CompanyId companyId,
        EmployeeId employeeId,
        RoleCode roleCode,
        PermissionScope scopeType,
        Guid? scopeId,
        DateTimeOffset assignedAtUtc,
        EmployeeId? assignedBy = null)
    {
        if (companyId.IsEmpty || employeeId.IsEmpty || roleCode.IsEmpty)
        {
            throw new ArgumentException("A role assignment needs a Company, an Employee, and a role.");
        }

        // A Team-scoped assignment with no Team reaches nothing; a Company-scoped one with a Team
        // suggests a limit that is not enforced. Both are configuration that reads as one thing
        // and behaves as another.
        if (scopeType is PermissionScope.Team && scopeId is null)
        {
            throw new ArgumentException(
                "A Team-scoped assignment must name a Team.", nameof(scopeId));
        }

        if (scopeType is not PermissionScope.Team && scopeId is not null)
        {
            throw new ArgumentException(
                $"A {scopeType}-scoped assignment must not name a Team.", nameof(scopeId));
        }

        return new EmployeeRole(
            Guid.CreateVersion7(), companyId, employeeId, roleCode, scopeType, scopeId, assignedAtUtc)
        {
            CreatedByEmployeeId = assignedBy
        };
    }
}
