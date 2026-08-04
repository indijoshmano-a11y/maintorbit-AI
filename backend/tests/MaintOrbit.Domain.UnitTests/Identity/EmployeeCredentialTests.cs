using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the credential aggregate.
/// </summary>
public sealed class EmployeeCredentialTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly PasswordHash Hash =
        PasswordHash.Create("$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA");
    private static readonly DateTimeOffset At = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private const string Parameters = "m=65536,t=3,p=4";

    private static EmployeeCredential Establish() =>
        EmployeeCredential.Establish(
            Company, Employee, Hash, PasswordAlgorithm.Argon2id, 1, Parameters, At);

    [Fact]
    public void Establish_CreatesACredentialForTheEmployee()
    {
        var credential = Establish();

        Assert.Equal(Company, credential.CompanyId);
        Assert.Equal(Employee, credential.EmployeeId);
        Assert.Equal(Hash, credential.PasswordHash);
    }

    [Fact]
    public void Establish_RecordsTheAlgorithmAndItsParameters()
    {
        // §4.2: hash_parameters is stored per row so an annual parameter review (SD-010) does not
        // invalidate existing hashes. A row that did not say how it was produced would become
        // unverifiable the moment the parameters changed.
        var credential = Establish();

        Assert.Equal(PasswordAlgorithm.Argon2id, credential.Algorithm);
        Assert.Equal(1, credential.PasswordVersion);
        Assert.Equal(Parameters, credential.HashParameters);
    }

    [Fact]
    public void Establish_SetsPasswordChangedToTheSuppliedTime()
    {
        // No clock is read here (U-3, AT-9), which is what makes this assertable.
        var credential = Establish();

        Assert.Equal(At, credential.PasswordChangedAtUtc);
        Assert.Equal(At, credential.CreatedAtUtc);
    }

    [Fact]
    public void NewCredential_IsUnlockedAndNotFlaggedForChange()
    {
        var credential = Establish();

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.RequirePasswordChange);
    }

    [Fact]
    public void Establish_RejectsACredentialWithNoCompany()
    {
        Assert.Throws<ArgumentException>(() => EmployeeCredential.Establish(
            CompanyId.Empty, Employee, Hash, PasswordAlgorithm.Argon2id, 1, Parameters, At));
    }

    [Fact]
    public void Establish_RejectsACredentialWithNoEmployee()
    {
        // Such a row authenticates nobody and is unreachable, but it is still a hash sitting in
        // the most sensitive table in the schema.
        Assert.Throws<ArgumentException>(() => EmployeeCredential.Establish(
            Company, EmployeeId.Empty, Hash, PasswordAlgorithm.Argon2id, 1, Parameters, At));
    }

    [Fact]
    public void Establish_RejectsAMissingHash()
    {
        Assert.Throws<ArgumentNullException>(() => EmployeeCredential.Establish(
            Company, Employee, null!, PasswordAlgorithm.Argon2id, 1, Parameters, At));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Establish_RejectsBlankHashParameters(string? parameters)
    {
        Assert.ThrowsAny<ArgumentException>(() => EmployeeCredential.Establish(
            Company, Employee, Hash, PasswordAlgorithm.Argon2id, 1, parameters!, At));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Establish_RejectsANonPositiveVersion(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EmployeeCredential.Establish(
            Company, Employee, Hash, PasswordAlgorithm.Argon2id, version, Parameters, At));
    }

    [Fact]
    public void Establish_RecordsWhoSetTheCredential()
    {
        var administrator = EmployeeId.New();

        var credential = EmployeeCredential.Establish(
            Company, Employee, Hash, PasswordAlgorithm.Argon2id, 1, Parameters, At, administrator);

        Assert.Equal(administrator, credential.CreatedByEmployeeId);
    }

    // ---- The separation itself ----------------------------------------------------------------

    [Theory]
    [InlineData("Password")]
    [InlineData("PasswordHash")]
    [InlineData("Salt")]
    [InlineData("FailedLoginCount")]
    [InlineData("LockoutUntilUtc")]
    [InlineData("MfaSecret")]
    [InlineData("RequirePasswordChange")]
    public void Employee_HoldsNoCredentialState(string forbidden)
    {
        // The separation is the security control, not a modelling preference. An ordinary
        // Employee read — a directory listing, a profile — must not be able to carry C4 material
        // into memory, and it cannot if the aggregate has nowhere to put it.
        Assert.Null(typeof(Employee).GetProperty(forbidden));
    }

    [Theory]
    [InlineData("FailedLoginCount")]
    [InlineData("LockoutUntilUtc")]
    [InlineData("PasswordHash")]
    [InlineData("RequirePasswordChange")]
    public void Credential_ExposesNoPublicSetterForItsProtectedState(string property)
    {
        // This replaces 11.3's "no lockout logic at all" gate, which FR-AUTH-011's counting has
        // now made obsolete — that assertion existed to stop the transitions arriving early and
        // being applied inconsistently, and they have arrived, in one place.
        //
        // What still matters is that they are the *only* way in. A public setter would let a
        // caller clear a lockout or zero a counter without going through the rules that decide
        // when either is legitimate.
        Assert.False(typeof(EmployeeCredential).GetProperty(property)!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Credential_MutatesLockoutStateOnlyThroughItsNamedTransitions()
    {
        // Three, and no more: a failure, a success, and a password change. Each one is a rule
        // about when the state may move, and a fourth added without a rule is how the counter
        // ends up cleared by something that never established the holder was present.
        var mutators = typeof(EmployeeCredential).GetMethods()
            .Where(method => method.DeclaringType == typeof(EmployeeCredential))
            .Where(method => method.Name is "RecordFailedAttempt"
                          or "RecordSuccessfulAttempt"
                          or "ChangePassword"
                          or "Establish")
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["ChangePassword", "Establish", "RecordFailedAttempt", "RecordSuccessfulAttempt"],
            mutators);
    }
}
