using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers failed-attempt counting and lockout (FR-AUTH-011).
/// </summary>
/// <remarks>
/// The interesting cases are the two failure directions. A lockout that never releases turns the
/// control into the denial-of-service 07-api-security T-3 warns about; one that releases without
/// resetting its counter re-locks on the next mistyped password, which is the same thing arriving
/// more slowly.
/// </remarks>
public sealed class AccountLockoutTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private const int Threshold = 3;

    private static EmployeeCredential Credential() =>
        EmployeeCredential.Establish(
            Company,
            Employee,
            PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$aGFzaA"),
            PasswordAlgorithm.Argon2id,
            1,
            "m=19456,t=3,p=1",
            Now);

    // ---- Counting -------------------------------------------------------------------------------

    [Fact]
    public void ANewCredentialHasNoFailuresAndNoLockout()
    {
        var credential = Credential();

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.IsLockedOut(Now));
    }

    [Fact]
    public void EachFailureIncrementsTheCount()
    {
        var credential = Credential();

        Assert.False(credential.RecordFailedAttempt(Threshold, Lockout, Now));
        Assert.Equal(1, credential.FailedLoginCount);

        Assert.False(credential.RecordFailedAttempt(Threshold, Lockout, Now.AddSeconds(1)));
        Assert.Equal(2, credential.FailedLoginCount);
    }

    [Fact]
    public void FailuresBelowTheThresholdDoNotLock()
    {
        var credential = Credential();

        credential.RecordFailedAttempt(Threshold, Lockout, Now);
        credential.RecordFailedAttempt(Threshold, Lockout, Now);

        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.IsLockedOut(Now));
    }

    [Fact]
    public void ReachingTheThresholdLocksForTheConfiguredDuration()
    {
        var credential = Credential();

        credential.RecordFailedAttempt(Threshold, Lockout, Now);
        credential.RecordFailedAttempt(Threshold, Lockout, Now);

        Assert.True(credential.RecordFailedAttempt(Threshold, Lockout, Now));

        Assert.Equal(Now.Add(Lockout), credential.LockoutUntilUtc);
        Assert.True(credential.IsLockedOut(Now));
    }

    [Fact]
    public void TheThresholdIsTheCompanysNotTheAggregates()
    {
        // FR-AUTH-011 makes the number configurable, and the caller supplies it — a credential that
        // knew its own threshold would need reloading whenever the policy changed.
        var strict = Credential();
        var lenient = Credential();

        Assert.True(strict.RecordFailedAttempt(maximumAttempts: 1, Lockout, Now));

        for (var attempt = 1; attempt < 10; attempt++)
        {
            Assert.False(lenient.RecordFailedAttempt(maximumAttempts: 10, Lockout, Now));
        }

        Assert.True(lenient.RecordFailedAttempt(maximumAttempts: 10, Lockout, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImpossibleThresholdIsRefused(int attempts)
    {
        // A threshold of zero would lock every account on its first failure, including one that
        // never failed. That is a configuration fault, not a state the aggregate should model.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Credential().RecordFailedAttempt(attempts, Lockout, Now));
    }

    [Fact]
    public void ALockoutWithNoDurationIsRefused()
    {
        // Zero minutes is a lockout that has already ended, which reads as a control and is none.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Credential().RecordFailedAttempt(Threshold, TimeSpan.Zero, Now));
    }

    // ---- Automatic unlock -------------------------------------------------------------------------

    [Fact]
    public void ALockoutEndsWhenTheClockPassesIt()
    {
        // Nothing sweeps expired lockouts and nothing needs to: the lockout is a timestamp, so it
        // stops being in force the moment the clock passes it.
        var credential = LockedCredential();

        Assert.True(credential.IsLockedOut(Now.Add(Lockout).AddTicks(-1)));

        // Exclusive at the boundary: the instant it ends, it has.
        Assert.False(credential.IsLockedOut(Now.Add(Lockout)));
        Assert.False(credential.IsLockedOut(Now.Add(Lockout).AddMinutes(1)));
    }

    [Fact]
    public void AnExpiredLockoutStartsAFreshWindow()
    {
        // The case that matters most. Without the reset the counter would still sit at the
        // threshold when the lockout lapsed, and the next mistyped password would re-lock
        // immediately — an account effectively locked forever after one bad afternoon.
        var credential = LockedCredential();
        var afterwards = Now.Add(Lockout).AddMinutes(1);

        Assert.False(credential.RecordFailedAttempt(Threshold, Lockout, afterwards));

        Assert.Equal(1, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.IsLockedOut(afterwards));
    }

    [Fact]
    public void AFreshWindowStillLocksOnceItsOwnThresholdIsReached()
    {
        var credential = LockedCredential();
        var afterwards = Now.Add(Lockout).AddMinutes(1);

        credential.RecordFailedAttempt(Threshold, Lockout, afterwards);
        credential.RecordFailedAttempt(Threshold, Lockout, afterwards);

        Assert.True(credential.RecordFailedAttempt(Threshold, Lockout, afterwards));
        Assert.Equal(afterwards.Add(Lockout), credential.LockoutUntilUtc);
    }

    [Fact]
    public void FailingWhileStillLockedExtendsNothingByItself()
    {
        // The aggregate would extend it — which is why the handler does not call this while a
        // lockout is in force. Asserted so the aggregate's behaviour is on record: an attacker who
        // could reach it would keep an account locked indefinitely by continuing to knock.
        var credential = LockedCredential();
        var during = Now.AddMinutes(1);

        credential.RecordFailedAttempt(Threshold, Lockout, during);

        Assert.Equal(during.Add(Lockout), credential.LockoutUntilUtc);
        Assert.Equal(Threshold + 1, credential.FailedLoginCount);
    }

    // ---- Success resets ----------------------------------------------------------------------------

    [Fact]
    public void ASuccessClearsTheCount()
    {
        // Without this the count would accumulate across weeks of ordinary typing mistakes and
        // lock an account that was never under attack.
        var credential = Credential();

        credential.RecordFailedAttempt(Threshold, Lockout, Now);
        credential.RecordFailedAttempt(Threshold, Lockout, Now);

        credential.RecordSuccessfulAttempt(Now.AddMinutes(1));

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.Equal(Now.AddMinutes(1), credential.UpdatedAtUtc);
    }

    [Fact]
    public void ASuccessAfterAnExpiredLockoutClearsIt()
    {
        var credential = LockedCredential();

        credential.RecordSuccessfulAttempt(Now.Add(Lockout).AddMinutes(1));

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
    }

    [Fact]
    public void ASuccessOnACleanCredentialWritesNothing()
    {
        // The common case. Marking the row dirty on every sign-in would turn an ordinary
        // authentication into a write on the most sensitive table in the schema.
        var credential = Credential();

        credential.RecordSuccessfulAttempt(Now.AddDays(1));

        Assert.Equal(Now, credential.UpdatedAtUtc);
    }

    [Fact]
    public void ThePasswordChangingAlsoClearsTheLockout()
    {
        // Established in 11.12 and asserted here alongside the rest: a reset completed through a
        // verified address is proof of control, and leaving the counter would lock the holder out
        // of the password they just set.
        var credential = LockedCredential();

        credential.ChangePassword(
            PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$bmV3"),
            PasswordAlgorithm.Argon2id, 1, "m=19456,t=3,p=1", Now.AddMinutes(1));

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.IsLockedOut(Now.AddMinutes(1)));
    }

    private static EmployeeCredential LockedCredential()
    {
        var credential = Credential();

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            credential.RecordFailedAttempt(Threshold, Lockout, Now);
        }

        return credential;
    }
}
