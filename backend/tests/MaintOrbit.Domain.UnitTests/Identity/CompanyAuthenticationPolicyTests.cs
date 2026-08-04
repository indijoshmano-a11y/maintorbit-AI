using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the Company authentication policy's bounds.
/// </summary>
/// <remarks>
/// Every one of these is a security control expressed as a number, which is the kind that looks
/// configured and does nothing when the bound is missing. The aggregate is where they are stated;
/// the database repeats them as check constraints, and the endpoint repeats them again as field
/// ranges — three layers, because a policy is read by code that trusts it.
/// </remarks>
public sealed class CompanyAuthenticationPolicyTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private static Result<CompanyAuthenticationPolicy> Create(
        int minimumPasswordLength = 12,
        int idleTimeoutMinutes = 60,
        int absoluteLifetimeMinutes = 720,
        int maximumFailedAttempts = 5,
        int lockoutMinutes = 15,
        bool mfaRequired = false,
        CompanyId? companyId = null) =>
        CompanyAuthenticationPolicy.Create(
            companyId ?? Company,
            minimumPasswordLength,
            requireBreachCheck: true,
            idleTimeoutMinutes,
            absoluteLifetimeMinutes,
            mfaRequired,
            maximumFailedAttempts,
            lockoutMinutes,
            Now);

    // ---- The defaults ---------------------------------------------------------------------------

    [Fact]
    public void TheDefaultPolicyIsOneACompanyCouldSave()
    {
        // The fallback for a Company with no row. If it were not itself valid, "unconfigured" and
        // "configured to the defaults" would behave differently — and the difference would appear
        // the first time somebody opened the settings page and pressed save.
        var defaults = CompanyAuthenticationPolicy.Default(Company);

        var equivalent = Create(
            defaults.MinimumPasswordLength,
            defaults.IdleTimeoutMinutes,
            defaults.AbsoluteLifetimeMinutes,
            defaults.MaximumFailedAttempts,
            defaults.LockoutMinutes);

        Assert.True(equivalent.IsSuccess);
    }

    [Fact]
    public void TheDefaultPolicyDoesNotRequireASecondFactor()
    {
        // A default of true would require every Employee to enrol before doing anything, including
        // the first administrator of a new Company — who would have nobody to turn it off.
        Assert.False(CompanyAuthenticationPolicy.Default(Company).MfaRequired);
    }

    // ---- Password policy ------------------------------------------------------------------------

    [Fact]
    public void APolicyMayBeStricterThanThePlatformFloorButNotWeaker()
    {
        // compliance §14 makes length the dial. A Company may raise it; lowering it below the
        // platform's floor would make the setting a way to opt out of the control.
        Assert.True(Create(minimumPasswordLength: 64).IsSuccess);

        Assert.True(Create(
            minimumPasswordLength: CompanyAuthenticationPolicy.MinimumAllowedPasswordLength - 1)
            .IsFailure);

        Assert.True(Create(minimumPasswordLength: 1).IsFailure);
        Assert.True(Create(minimumPasswordLength: 0).IsFailure);
    }

    [Fact]
    public void AnUnsatisfiablePasswordLengthIsRefused()
    {
        // A ceiling because a policy nobody can satisfy is a Company that cannot onboard.
        Assert.True(Create(
            minimumPasswordLength: CompanyAuthenticationPolicy.MaximumAllowedPasswordLength + 1)
            .IsFailure);
    }

    [Fact]
    public void ThePolicyJudgesAPasswordByLength()
    {
        var policy = Create(minimumPasswordLength: 16).Value;

        Assert.False(policy.IsPasswordLongEnough(15));
        Assert.True(policy.IsPasswordLongEnough(16));
        Assert.True(policy.IsPasswordLongEnough(100));
    }

    // ---- Session timers -------------------------------------------------------------------------

    [Fact]
    public void TheAbsoluteLifetimeMayNotBeShorterThanTheIdleWindow()
    {
        // §3.2 calls the absolute lifetime "the one that cannot be defeated by activity". Shorter
        // than the idle window, it makes the idle window unreachable — the session always ends
        // first, and the idle setting reads as configured while doing nothing.
        Assert.True(Create(idleTimeoutMinutes: 60, absoluteLifetimeMinutes: 30).IsFailure);

        // Equal is permitted: the two simply coincide.
        Assert.True(Create(idleTimeoutMinutes: 60, absoluteLifetimeMinutes: 60).IsSuccess);
        Assert.True(Create(idleTimeoutMinutes: 60, absoluteLifetimeMinutes: 61).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(43_201)]
    public void AnIdleWindowOutsideItsBoundsIsRefused(int minutes)
    {
        Assert.True(Create(idleTimeoutMinutes: minutes, absoluteLifetimeMinutes: 43_200).IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(43_201)]
    public void AnAbsoluteLifetimeOutsideItsBoundsIsRefused(int minutes)
    {
        Assert.True(Create(idleTimeoutMinutes: 5, absoluteLifetimeMinutes: minutes).IsFailure);
    }

    // ---- Lockout ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(21)]
    [InlineData(1_000)]
    public void ALockoutThresholdOutsideItsBoundsIsRefused(int attempts)
    {
        // A threshold of a thousand is one an online guessing attack never reaches, and the
        // setting would read as protection while providing none. A threshold of zero locks
        // everybody out immediately.
        Assert.True(Create(maximumFailedAttempts: attempts).IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_441)]
    public void ALockoutDurationOutsideItsBoundsIsRefused(int minutes)
    {
        Assert.True(Create(lockoutMinutes: minutes).IsFailure);
    }

    [Fact]
    public void AThresholdInsideItsBoundsIsAccepted()
    {
        Assert.True(Create(maximumFailedAttempts: 3, lockoutMinutes: 1).IsSuccess);
        Assert.True(Create(maximumFailedAttempts: 20, lockoutMinutes: 1_440).IsSuccess);
    }

    // ---- Ownership --------------------------------------------------------------------------------

    [Fact]
    public void APolicyMustBelongToACompany()
    {
        Assert.True(Create(companyId: CompanyId.Empty).IsFailure);
    }

    // ---- Update ------------------------------------------------------------------------------------

    [Fact]
    public void UpdatingReplacesEverySettingAndRecordsWho()
    {
        var policy = Create().Value;
        var actor = Domain.Modules.Identity.ValueObjects.EmployeeId.New();

        var result = policy.Update(
            minimumPasswordLength: 20,
            requireBreachCheck: false,
            idleTimeoutMinutes: 15,
            absoluteLifetimeMinutes: 480,
            mfaRequired: true,
            maximumFailedAttempts: 3,
            lockoutMinutes: 30,
            updatedAtUtc: Now.AddDays(1),
            updatedBy: actor);

        Assert.True(result.IsSuccess);
        Assert.Equal(20, policy.MinimumPasswordLength);
        Assert.False(policy.RequireBreachCheck);
        Assert.Equal(15, policy.IdleTimeoutMinutes);
        Assert.Equal(480, policy.AbsoluteLifetimeMinutes);
        Assert.True(policy.MfaRequired);
        Assert.Equal(3, policy.MaximumFailedAttempts);
        Assert.Equal(30, policy.LockoutMinutes);
        Assert.Equal(Now.AddDays(1), policy.UpdatedAtUtc);
        Assert.Equal(actor, policy.UpdatedByEmployeeId);
    }

    [Fact]
    public void ARejectedUpdateChangesNothing()
    {
        // The aggregate validates before it mutates, so a refused save leaves the policy that was
        // in force rather than a half-applied one.
        var policy = Create(minimumPasswordLength: 20).Value;

        var result = policy.Update(
            minimumPasswordLength: 4,
            requireBreachCheck: true,
            idleTimeoutMinutes: 60,
            absoluteLifetimeMinutes: 720,
            mfaRequired: false,
            maximumFailedAttempts: 5,
            lockoutMinutes: 15,
            updatedAtUtc: Now.AddDays(1));

        Assert.True(result.IsFailure);
        Assert.Equal(20, policy.MinimumPasswordLength);
        Assert.Equal(Now, policy.UpdatedAtUtc);
    }

    [Fact]
    public void EveryBoundAppliesToUpdateAsWellAsCreate()
    {
        // A bound enforced on creation and not on update is a bound an administrator steps around
        // by saving twice.
        var policy = Create().Value;

        Assert.True(policy.Update(
            12, true, 60, 30, false, 5, 15, Now).IsFailure);

        Assert.True(policy.Update(
            12, true, 60, 720, false, 100, 15, Now).IsFailure);

        Assert.True(policy.Update(
            12, true, 60, 720, false, 5, 100_000, Now).IsFailure);
    }
}
