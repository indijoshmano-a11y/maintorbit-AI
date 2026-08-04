using MaintOrbit.Api.Authorization;
using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Authorization;
using MaintOrbit.Infrastructure.Caching;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.FunctionalTests.Authorization;

/// <summary>
/// Covers permission resolution and evaluation.
/// </summary>
/// <remarks>
/// Deny by default (SD-001) means most of what matters here is what is <i>not</i> granted, so the
/// negative cases carry the weight: an unknown permission, a role with no grants, and a scope that
/// does not reach must all be refusals rather than oversights.
/// </remarks>
public sealed class PermissionResolutionTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    // The three codes §4.2 actually names. No others are invented — a permission that does not
    // exist in the documentation is a permission nothing should be checking.
    private static readonly PermissionCode CreateConnection =
        PermissionCode.Create("provider-connection.create");
    private static readonly PermissionCode ManageBudget = PermissionCode.Create("budget.manage");
    private static readonly PermissionCode ReadAudit = PermissionCode.Create("audit.read");

    private static readonly RoleCode Admin = RoleCode.Create("company-admin");
    private static readonly RoleCode Billing = RoleCode.Create("billing-admin");
    private static readonly RoleCode Lead = RoleCode.Create("team-lead");

    private sealed class Fixture
    {
        public FakeAuthorizationRepository Repository { get; } = new();

        public PermissionService Service() => new PermissionService(Repository, new DisabledPermissionCache());

        public Fixture Grant(RoleCode role, params PermissionCode[] permissions)
        {
            foreach (var permission in permissions)
            {
                Repository.Grants.Add(RolePermission.Grant(role, permission));
            }

            return this;
        }

        public Fixture Assign(RoleCode role, PermissionScope scope = PermissionScope.Company, Guid? scopeId = null)
        {
            Repository.Assignments.Add(
                EmployeeRole.Assign(Company, Employee, role, scope, scopeId, Now));

            return this;
        }

        public Task<EmployeePermissions> ResolveAsync() =>
            Service().ResolveAsync(Employee, Company, CancellationToken.None);
    }

    // ---- Resolution -------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmployeeWithNoRoles_HoldsNothing()
    {
        // SD-001: absence of a grant is refusal, and holding no roles is the purest form of it.
        var permissions = await new Fixture().ResolveAsync();

        Assert.Empty(permissions.Permissions);
        Assert.False(permissions.IsGranted(ReadAudit, PermissionScope.Company));
    }

    [Fact]
    public async Task ARolesPermissions_AreResolved()
    {
        var permissions = await new Fixture()
            .Grant(Admin, CreateConnection, ManageBudget)
            .Assign(Admin)
            .ResolveAsync();

        Assert.True(permissions.IsGranted(CreateConnection, PermissionScope.Company));
        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Company));
    }

    [Fact]
    public async Task MultipleRoles_Union()
    {
        // §3.4 permits several roles and states there is no hierarchy, so holding two grants both
        // sets — there is nothing to resolve between them.
        var permissions = await new Fixture()
            .Grant(Admin, CreateConnection)
            .Grant(Billing, ManageBudget)
            .Assign(Admin)
            .Assign(Billing)
            .ResolveAsync();

        Assert.True(permissions.IsGranted(CreateConnection, PermissionScope.Company));
        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Company));
    }

    [Fact]
    public async Task RolesAreIncomparable_NotHierarchical()
    {
        // §3.4: "Billing Admin genuinely cannot see Provider Connections", and a linear hierarchy
        // would grant it access it has no business having.
        var permissions = await new Fixture()
            .Grant(Admin, CreateConnection)
            .Grant(Billing, ManageBudget)
            .Assign(Billing)
            .ResolveAsync();

        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Company));
        Assert.False(permissions.IsGranted(CreateConnection, PermissionScope.Company));
    }

    [Fact]
    public async Task ADuplicatePermissionAcrossRoles_IsHeldOnce()
    {
        var permissions = await new Fixture()
            .Grant(Admin, ReadAudit)
            .Grant(Billing, ReadAudit)
            .Assign(Admin)
            .Assign(Billing)
            .ResolveAsync();

        Assert.Single(permissions.Permissions);
        Assert.True(permissions.IsGranted(ReadAudit, PermissionScope.Company));
    }

    [Fact]
    public async Task ARoleWithNoGrants_ContributesNothing()
    {
        // Possible while a custom role is being composed (FR-PERM-006). Harmless: it grants
        // nothing, which is what deny-by-default already assumes.
        var permissions = await new Fixture().Assign(Admin).ResolveAsync();

        Assert.Empty(permissions.Permissions);
    }

    [Fact]
    public async Task RemovingAnAssignment_RemovesThePermission()
    {
        // The "revoked role" case. Nothing is cached, so the next resolution reflects it —
        // FR-PERM-005's 60 seconds is satisfied trivially while there is no cache.
        var fixture = new Fixture().Grant(Admin, ReadAudit).Assign(Admin);

        Assert.True((await fixture.ResolveAsync()).IsGranted(ReadAudit, PermissionScope.Company));

        fixture.Repository.Assignments.Clear();

        Assert.False((await fixture.ResolveAsync()).IsGranted(ReadAudit, PermissionScope.Company));
    }

    [Fact]
    public async Task AnUnknownPermission_IsRefused()
    {
        var permissions = await new Fixture().Grant(Admin, ReadAudit).Assign(Admin).ResolveAsync();

        Assert.False(permissions.IsGranted(
            PermissionCode.Create("nothing.granted"), PermissionScope.Company));
    }

    // ---- Scope --------------------------------------------------------------------------------------

    [Fact]
    public async Task ACompanyScopedGrant_ReachesEverything()
    {
        var permissions = await new Fixture()
            .Grant(Admin, ManageBudget).Assign(Admin).ResolveAsync();

        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Company));
        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Team, Guid.CreateVersion7()));
        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Self));
    }

    [Fact]
    public async Task ATeamScopedGrant_ReachesOnlyThatTeam()
    {
        var team = Guid.CreateVersion7();
        var otherTeam = Guid.CreateVersion7();

        var permissions = await new Fixture()
            .Grant(Lead, ManageBudget)
            .Assign(Lead, PermissionScope.Team, team)
            .ResolveAsync();

        Assert.True(permissions.IsGranted(ManageBudget, PermissionScope.Team, team));
        Assert.False(permissions.IsGranted(ManageBudget, PermissionScope.Team, otherTeam));

        // And must not widen: a Team Lead does not administer the Company.
        Assert.False(permissions.IsGranted(ManageBudget, PermissionScope.Company));
    }

    [Fact]
    public async Task ATeamScopedGrant_SatisfiesNoUntargetedRequest()
    {
        var permissions = await new Fixture()
            .Grant(Lead, ManageBudget)
            .Assign(Lead, PermissionScope.Team, Guid.CreateVersion7())
            .ResolveAsync();

        Assert.False(permissions.IsGranted(ManageBudget, PermissionScope.Team, target: null));
    }

    [Fact]
    public void AnAssignment_RefusesAScopeAndTargetThatDisagree()
    {
        // Team-scoped with no Team reaches nothing; anything else carrying one implies a limit
        // that is not enforced.
        Assert.Throws<ArgumentException>(() =>
            EmployeeRole.Assign(Company, Employee, Lead, PermissionScope.Team, null, Now));

        Assert.Throws<ArgumentException>(() =>
            EmployeeRole.Assign(
                Company, Employee, Admin, PermissionScope.Company, Guid.CreateVersion7(), Now));
    }

    // ---- Policy names ---------------------------------------------------------------------------------

    [Fact]
    public void APolicyName_RoundTripsToItsRequirement()
    {
        var name = PermissionRequirement.PolicyName(CreateConnection, PermissionScope.Team);

        var parsed = PermissionRequirement.TryParse(name);

        Assert.NotNull(parsed);
        Assert.Equal(CreateConnection, parsed.Permission);
        Assert.Equal(PermissionScope.Team, parsed.Scope);
    }

    [Theory]
    [InlineData("permission:not a code:Company")]
    [InlineData("permission:budget.manage:Nowhere")]
    [InlineData("permission:budget.manage")]
    [InlineData("SomeOtherPolicy")]
    public void AnUnparseablePolicyName_YieldsNoRequirement(string name)
    {
        // A malformed name must not become a permissive policy. Returning nothing sends it to the
        // default provider, which has no such policy — a denial either way.
        Assert.Null(PermissionRequirement.TryParse(name));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("budget.")]
    [InlineData(".manage")]
    [InlineData("budget.manage.extra")]
    [InlineData("Budget.Manage")]
    [InlineData("budget manage")]
    public void AMalformedPermissionCode_IsRejected(string candidate)
    {
        // Under deny-by-default a typo is not an error, it is a silent refusal — so the shape is
        // checked where the code is created rather than discovered as a permission nobody holds.
        Assert.False(PermissionCode.TryCreate(candidate, out _));
    }

    // ---- Fake -------------------------------------------------------------------------------------------

    private sealed class FakeAuthorizationRepository : IAuthorizationRepository
    {
        public List<EmployeeRole> Assignments { get; } = [];

        public List<RolePermission> Grants { get; } = [];

        public Task<IReadOnlyList<EmployeeRole>> FindRolesForAsync(
            EmployeeId employeeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmployeeRole>>(
                Assignments.Where(role => role.EmployeeId == employeeId).ToList());

        public Task<IReadOnlyList<RolePermission>> FindPermissionsForRolesAsync(
            IReadOnlyCollection<RoleCode> roleCodes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RolePermission>>(
                Grants.Where(grant => roleCodes.Contains(grant.RoleCode)).ToList());

        public void Add(EmployeeRole assignment) => Assignments.Add(assignment);

        public void Remove(EmployeeRole assignment) => Assignments.Remove(assignment);

        public Task<EmployeeRole?> FindAssignmentAsync(
            Guid assignmentId, CancellationToken cancellationToken) =>
            Task.FromResult(Assignments.FirstOrDefault(role => role.Id == assignmentId));

        public Task<bool> AssignmentExistsAsync(
            EmployeeId employeeId,
            RoleCode roleCode,
            PermissionScope scopeType,
            Guid? scopeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Assignments.Any(role =>
                role.EmployeeId == employeeId &&
                role.RoleCode == roleCode &&
                role.ScopeType == scopeType &&
                role.ScopeId == scopeId));

        // Every role these tests name is treated as defined. Whether a role exists is asserted
        // against the real catalogue by the assignment tests, which have a database.
        public Task<bool> RoleExistsAsync(RoleCode roleCode, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
