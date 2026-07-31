using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the Employee aggregate's invariants.
/// </summary>
public sealed class EmployeeTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly Email Address = Email.Create("ada@example.com");
    private static readonly DateTimeOffset InvitedAt =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invite_CreatesAnInvitedEmployee()
    {
        // Nothing may create an already-active account: activation is what accepting an
        // invitation means, and skipping it would be an account nobody proved they control.
        var employee = Employee.Invite(Company, Address, InvitedAt);

        Assert.Equal(EmployeeStatus.Invited, employee.Status);
    }

    [Fact]
    public void Invite_AssignsTheCompanyAndAddress()
    {
        var employee = Employee.Invite(Company, Address, InvitedAt);

        Assert.Equal(Company, employee.CompanyId);
        Assert.Equal(Address, employee.Email);
    }

    [Fact]
    public void Invite_GeneratesATimeOrderedIdentifier()
    {
        // §1.6: UUIDv7. Version 7 is encoded in the seventh byte's high nibble.
        var employee = Employee.Invite(Company, Address, InvitedAt);

        var version = employee.Id.Value.ToByteArray(bigEndian: true)[6] >> 4;

        Assert.Equal(7, version);
        Assert.False(employee.Id.IsEmpty);
    }

    [Fact]
    public void Invite_GivesEachEmployeeADistinctIdentifier()
    {
        var identifiers = Enumerable.Range(0, 500)
            .Select(_ => Employee.Invite(Company, Address, InvitedAt).Id)
            .ToList();

        Assert.Equal(identifiers.Count, identifiers.Distinct().Count());
    }

    [Fact]
    public void Invite_RejectsAnEmployeeWithNoCompany()
    {
        // A row with no Company matches no tenant policy, so no caller could ever read it back.
        // Rejecting it here keeps it from reaching a NOT NULL column that a default Guid would
        // satisfy.
        Assert.Throws<ArgumentException>(
            () => Employee.Invite(CompanyId.Empty, Address, InvitedAt));
    }

    [Fact]
    public void Invite_RejectsAMissingAddress()
    {
        Assert.Throws<ArgumentNullException>(
            () => Employee.Invite(Company, null!, InvitedAt));
    }

    [Fact]
    public void Invite_UsesTheSuppliedTimestampForBothAuditFields()
    {
        // U-3 and AT-9: the aggregate never reads the ambient clock, which is also what makes
        // this assertable at all.
        var employee = Employee.Invite(Company, Address, InvitedAt);

        Assert.Equal(InvitedAt, employee.CreatedAtUtc);
        Assert.Equal(InvitedAt, employee.UpdatedAtUtc);
    }

    [Fact]
    public void Invite_RecordsWhoIssuedTheInvitation()
    {
        var inviter = EmployeeId.New();

        var employee = Employee.Invite(Company, Address, InvitedAt, inviter);

        Assert.Equal(inviter, employee.CreatedByEmployeeId);
        Assert.Equal(inviter, employee.UpdatedByEmployeeId);
    }

    [Fact]
    public void Invite_LeavesASystemCreatedEmployeeWithoutAnActor()
    {
        // §1.7 makes created_by_employee_id nullable precisely for rows the system creates —
        // the first Employee of a Company has nobody to attribute the invitation to.
        var employee = Employee.Invite(Company, Address, InvitedAt);

        Assert.Null(employee.CreatedByEmployeeId);
    }

    [Fact]
    public void NewEmployee_IsNeitherVerifiedNorDeletedNorPseudonymized()
    {
        var employee = Employee.Invite(Company, Address, InvitedAt);

        Assert.Null(employee.EmailVerifiedAtUtc);
        Assert.Null(employee.DeletedAtUtc);
        Assert.Null(employee.PseudonymizedAtUtc);
        Assert.False(employee.IsDeleted);
    }

    [Fact]
    public void CompanyAssignment_IsNotReachableAfterCreation()
    {
        // An Employee belongs to exactly one Company for its whole life. A settable discriminator
        // would make moving a row across a tenant boundary an ordinary assignment.
        var property = typeof(Employee).GetProperty(nameof(Employee.CompanyId))!;

        Assert.NotNull(property.SetMethod);
        Assert.True(property.SetMethod!.IsPrivate);
        Assert.Contains(
            property.SetMethod.ReturnParameter.GetRequiredCustomModifiers(),
            static modifier => modifier.Name == "IsExternalInit");
    }
}
