using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Common.Authorization;

/// <summary>
/// The permission codes the identity module's endpoints declare.
/// </summary>
/// <remarks>
/// <b>The codes are data; these are the names the code refers to them by.</b> The catalogue lives
/// in <c>identity.permissions</c> — SD-020 makes roles presets over permissions, and what a role
/// grants is a row, not a branch. What cannot live only in the database is the constant an
/// endpoint uses to say which permission it needs, and putting that in one place means a typo is a
/// compile error rather than a policy that silently denies everybody.
/// <para>
/// Names come from api-specification §3.2, which states the Employees group's permissions as
/// <c>employee.read [C]</c>, <c>employee.invite [C]</c>, and <c>employee.manage [C]</c>.
/// <b>Only the ones an endpoint actually declares appear here</b> — a constant for an operation
/// that does not exist is a claim about a surface that has not been built.
/// </para>
/// </remarks>
public static class IdentityPermissions
{
    /// <summary>
    /// Read Employees — <c>employee.read</c> (§3.2).
    /// </summary>
    /// <remarks>
    /// §3.2 gives it at two scopes: <c>[C]</c> for the directory and <c>[S]</c> for <c>/me</c>.
    /// One code, two scopes — the scope is the endpoint's declaration, not a second permission.
    /// </remarks>
    public static PermissionCode EmployeeRead { get; } = PermissionCode.Create("employee.read");
}
