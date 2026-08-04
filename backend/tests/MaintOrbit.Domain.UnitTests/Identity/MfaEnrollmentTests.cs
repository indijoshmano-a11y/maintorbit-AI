using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the MFA aggregates.
/// </summary>
/// <remarks>
/// 02-authentication-architecture §3.6 names four properties: the secret is encrypted at rest,
/// recovery codes are issued once, hashed, and single-use, and "a used TOTP code is rejected
/// within its window". The last one is a rule about state, so it lives here — as does the
/// pending/confirmed distinction that keeps an unproved secret from locking its owner out.
/// </remarks>
public sealed class MfaEnrollmentTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private static SecretEnvelope Envelope() =>
        SecretEnvelope.Create(
            [1, 2, 3, 4],
            new byte[SecretEnvelope.NonceLength],
            new byte[SecretEnvelope.TagLength],
            dekVersion: 1);

    private static MfaEnrollment Begin() =>
        MfaEnrollment.Begin(Company, Employee, MfaMethod.Totp, Envelope(), Now);

    private static MfaEnrollment Confirmed(long step = 100)
    {
        var enrollment = Begin();
        enrollment.TryConfirm(step, Now);
        return enrollment;
    }

    // ---- Enrolment ----------------------------------------------------------------------------

    [Fact]
    public void ANewEnrolment_IsPendingAndNotYetAFactor()
    {
        // A secret is generated before the Employee has proved their authenticator holds it. An
        // enrolment that counted from that moment would lock out anyone who scanned the code into
        // the wrong app — turning a second factor into a way to lose an account.
        var enrollment = Begin();

        Assert.True(enrollment.IsPending);
        Assert.False(enrollment.IsActive);
        Assert.Equal(MfaEnrollmentStatus.Pending, enrollment.Status);
        Assert.Null(enrollment.ConfirmedAtUtc);
        Assert.Null(enrollment.LastAcceptedTimeStep);
    }

    [Fact]
    public void AnEnrolment_MustBelongToACompanyAndAnEmployee()
    {
        Assert.Throws<ArgumentException>(() =>
            MfaEnrollment.Begin(CompanyId.Empty, Employee, MfaMethod.Totp, Envelope(), Now));

        Assert.Throws<ArgumentException>(() =>
            MfaEnrollment.Begin(Company, EmployeeId.Empty, MfaMethod.Totp, Envelope(), Now));
    }

    [Fact]
    public void AnEnrolment_CarriesATimeOrderedIdentifier()
    {
        Assert.NotEqual(Begin().Id, Begin().Id);
        Assert.False(Begin().Id.IsEmpty);
    }

    // ---- Confirmation -------------------------------------------------------------------------

    [Fact]
    public void Confirming_TurnsTheFactorOnAndSpendsTheProvingStep()
    {
        var enrollment = Begin();

        Assert.True(enrollment.TryConfirm(100, Now));

        Assert.True(enrollment.IsActive);
        Assert.False(enrollment.IsPending);
        Assert.Equal(Now, enrollment.ConfirmedAtUtc);
        Assert.Equal(Now, enrollment.LastVerifiedAtUtc);

        // The code that proved possession is spent by proving it, so it cannot be turned around
        // and replayed as a verification a moment later.
        Assert.Equal(100, enrollment.LastAcceptedTimeStep);
        Assert.False(enrollment.IsUnusedTimeStep(100));
    }

    [Fact]
    public void ConfirmingTwice_IsRefusedRatherThanThrowing()
    {
        // A duplicate request, not a fault — a client retrying after a dropped response should get
        // a refusal it can interpret, not an exception in a log.
        var enrollment = Confirmed();

        Assert.False(enrollment.TryConfirm(200, Now.AddMinutes(1)));
        Assert.Equal(100, enrollment.LastAcceptedTimeStep);
    }

    [Fact]
    public void ConfirmingADisabledEnrolment_IsRefused()
    {
        var enrollment = Begin();
        enrollment.Disable(Now);

        Assert.False(enrollment.TryConfirm(100, Now));
        Assert.False(enrollment.IsActive);
    }

    // ---- Replay -------------------------------------------------------------------------------

    [Fact]
    public void AFreshTimeStep_IsAccepted()
    {
        var enrollment = Confirmed(step: 100);

        Assert.True(enrollment.TryAcceptTimeStep(101, Now.AddSeconds(30)));
        Assert.Equal(101, enrollment.LastAcceptedTimeStep);
    }

    [Fact]
    public void TheSameTimeStepTwice_IsRefused()
    {
        // §3.6: "A used TOTP code is rejected within its window." A code is a function of the
        // secret and the step, so the same step presented twice is the same code presented twice.
        var enrollment = Confirmed(step: 100);

        Assert.True(enrollment.TryAcceptTimeStep(101, Now));
        Assert.False(enrollment.TryAcceptTimeStep(101, Now));
    }

    [Fact]
    public void AnEarlierTimeStep_IsRefused()
    {
        // The same replay with a delay. Strictly-greater, not greater-or-equal, is what closes it.
        var enrollment = Confirmed(step: 100);

        Assert.False(enrollment.TryAcceptTimeStep(99, Now));
        Assert.False(enrollment.TryAcceptTimeStep(1, Now));
        Assert.Equal(100, enrollment.LastAcceptedTimeStep);
    }

    [Fact]
    public void APendingEnrolment_AcceptsNoTimeStep()
    {
        // Verification against an unproved secret would let an enrolment nobody completed satisfy
        // a challenge.
        Assert.False(Begin().TryAcceptTimeStep(500, Now));
    }

    [Fact]
    public void ADisabledEnrolment_AcceptsNoTimeStep()
    {
        var enrollment = Confirmed();
        enrollment.Disable(Now);

        Assert.False(enrollment.TryAcceptTimeStep(500, Now.AddMinutes(1)));
    }

    // ---- Recovery -----------------------------------------------------------------------------

    [Fact]
    public void RecordingARecovery_SpendsNoTimeStep()
    {
        // A recovery code is its own single-use credential. Advancing the counter would invalidate
        // the authenticator's current code as a side effect of not having used it.
        var enrollment = Confirmed(step: 100);

        enrollment.RecordRecovery(Now.AddMinutes(5));

        Assert.Equal(100, enrollment.LastAcceptedTimeStep);
        Assert.Equal(Now.AddMinutes(5), enrollment.LastVerifiedAtUtc);
    }

    // ---- Disabling ----------------------------------------------------------------------------

    [Fact]
    public void Disabling_IsIdempotentAndKeepsTheFirstInstant()
    {
        var enrollment = Confirmed();

        enrollment.Disable(Now.AddMinutes(1));
        enrollment.Disable(Now.AddMinutes(9));

        Assert.Equal(MfaEnrollmentStatus.Disabled, enrollment.Status);
        Assert.Equal(Now.AddMinutes(1), enrollment.DisabledAtUtc);
        Assert.False(enrollment.IsActive);
        Assert.False(enrollment.IsPending);
    }

    // ---- Recovery codes -----------------------------------------------------------------------

    [Fact]
    public void ARecoveryCode_IsSpentOnce()
    {
        var code = IssuedCode();

        Assert.True(code.TryConsume(Now));
        Assert.True(code.IsUsed);
        Assert.Equal(Now, code.UsedAtUtc);
    }

    [Fact]
    public void AReusedRecoveryCode_IsRefused()
    {
        // §3.6 makes them single-use. A code that works twice is a permanent bypass to whoever
        // saw it once.
        var code = IssuedCode();

        Assert.True(code.TryConsume(Now));
        Assert.False(code.TryConsume(Now.AddMinutes(1)));

        // And the record of the first use is not overwritten by the second attempt.
        Assert.Equal(Now, code.UsedAtUtc);
    }

    [Fact]
    public void ARecoveryCode_MustBelongToAnEnrolment()
    {
        // Unbound, it would survive the factor it recovers and become a permanent bypass.
        var hash = RecoveryCodeHash.Create(new string('a', RecoveryCodeHash.Length));

        Assert.Throws<ArgumentException>(() =>
            MfaRecoveryCode.Issue(Company, Employee, MfaEnrollmentId.Empty, hash, Now));

        Assert.Throws<ArgumentException>(() =>
            MfaRecoveryCode.Issue(CompanyId.Empty, Employee, MfaEnrollmentId.New(), hash, Now));

        Assert.Throws<ArgumentException>(() =>
            MfaRecoveryCode.Issue(Company, EmployeeId.Empty, MfaEnrollmentId.New(), hash, Now));
    }

    // ---- Redaction ----------------------------------------------------------------------------

    [Fact]
    public void TheEnvelopeAndTheCodeHash_RefuseToPrintThemselves()
    {
        // Both sit on C4 tables, and a type that prints its contents is one an aggregate prints in
        // any log line that formats it.
        var hash = RecoveryCodeHash.Create(new string('a', RecoveryCodeHash.Length));

        Assert.Equal("[REDACTED]", Envelope().ToString());
        Assert.Equal("[REDACTED]", hash.ToString());
        Assert.DoesNotContain("aaaa", $"{hash}", StringComparison.Ordinal);
    }

    // ---- Envelope shape -----------------------------------------------------------------------

    [Fact]
    public void AnEnvelope_RefusesPartsThatCannotHaveComeFromAesGcm()
    {
        // Checked at construction rather than trusted, because the alternative failure is at
        // decryption time — on a row already written, for an Employee already locked out.
        var nonce = new byte[SecretEnvelope.NonceLength];
        var tag = new byte[SecretEnvelope.TagLength];

        Assert.Throws<ArgumentException>(() =>
            SecretEnvelope.Create([], nonce, tag, 1));

        Assert.Throws<ArgumentException>(() =>
            SecretEnvelope.Create([1], new byte[8], tag, 1));

        Assert.Throws<ArgumentException>(() =>
            SecretEnvelope.Create([1], nonce, new byte[8], 1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecretEnvelope.Create([1], nonce, tag, 0));
    }

    [Fact]
    public void AnEnvelope_RecordsItsKeyVersionAndAlgorithm()
    {
        // SD-012 and SD-009. Without the version, rotation would be a synchronized rewrite of
        // everything rather than an incremental one.
        var envelope = SecretEnvelope.Create(
            [1], new byte[SecretEnvelope.NonceLength], new byte[SecretEnvelope.TagLength], 7);

        Assert.Equal(7, envelope.DekVersion);
        Assert.Equal(SecretEnvelope.AesGcm256, envelope.AlgorithmId);
    }

    private static MfaRecoveryCode IssuedCode() =>
        MfaRecoveryCode.Issue(
            Company,
            Employee,
            MfaEnrollmentId.New(),
            RecoveryCodeHash.Create(new string('a', RecoveryCodeHash.Length)),
            Now);
}
