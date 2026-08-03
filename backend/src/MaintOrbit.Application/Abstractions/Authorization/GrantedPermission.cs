using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Authorization;

/// <summary>A permission an Employee holds, and how far it reaches.</summary>
/// <remarks>
/// Scope travels with the permission because §3.5 evaluates the two together. A Team Lead holding
/// <c>budget.manage</c> over one Team and a Company Admin holding it outright hold the same
/// permission and are not equivalent.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The rule guards against confusion with the legacy System.Security.Permissions " +
                    "types, which this codebase does not use. This is a permission, and calling it " +
                    "anything else to satisfy a suffix heuristic would obscure what it holds.")]
public readonly record struct GrantedPermission(
    PermissionCode Permission, PermissionScope Scope, Guid? ScopeId);

/// <summary>Everything an Employee is permitted to do, in one Company.</summary>
/// <remarks>
/// The whole set is resolved at once rather than a permission at a time: a request usually checks
/// one permission, but resolving the set makes the result cacheable per Employee, which is what
/// keeps a per-request check inside NFR-PERF-007's 10 ms budget.
/// </remarks>
public sealed class EmployeePermissions
{
    private readonly Dictionary<PermissionCode, List<GrantedPermission>> _byPermission;

    public EmployeePermissions(IEnumerable<GrantedPermission> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);

        _byPermission = granted
            .GroupBy(static grant => grant.Permission)
            .ToDictionary(static group => group.Key, static group => group.ToList());
    }

    /// <summary>An Employee with no roles — the deny-by-default starting point (SD-001).</summary>
    public static EmployeePermissions None { get; } = new([]);

    /// <summary>Every distinct permission held, regardless of scope.</summary>
    public IReadOnlyCollection<PermissionCode> Permissions => _byPermission.Keys;

    /// <summary>
    /// Whether the Employee holds a permission at a scope that satisfies the request.
    /// </summary>
    /// <remarks>
    /// Deny by default (SD-001, FR-PERM-002): absence of a grant is refusal, so an unknown
    /// permission and a permission nobody granted are the same answer.
    /// <para>
    /// A Company-scoped grant satisfies any request. A Team-scoped grant satisfies a request only
    /// for that Team. A Self-scoped grant satisfies only a request about the Employee themselves,
    /// which the caller expresses by asking for <see cref="PermissionScope.Self"/>.
    /// </para>
    /// </remarks>
    public bool IsGranted(PermissionCode permission, PermissionScope required, Guid? target = null)
    {
        if (!_byPermission.TryGetValue(permission, out var grants))
        {
            return false;
        }

        foreach (var grant in grants)
        {
            if (Satisfies(grant, required, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Satisfies(GrantedPermission grant, PermissionScope required, Guid? target) =>
        grant.Scope switch
        {
            // Company-wide: reaches everything in the Company, including a specific Team or the
            // Employee's own records.
            PermissionScope.Company => true,

            // Team-scoped: reaches a request about that Team, and nothing broader.
            PermissionScope.Team =>
                required is PermissionScope.Team && target is not null && grant.ScopeId == target,

            // Self-scoped: reaches only a request the caller made about themselves.
            PermissionScope.Self => required is PermissionScope.Self,

            _ => false
        };
}
