using System.Security.Cryptography;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Modules.Identity.Commands.Mfa;

/// <summary>
/// Issues a TOTP secret for the authenticated Employee (FR-AUTH-005).
/// </summary>
/// <remarks>
/// The Employee and Company come from the validated access token
/// (<see cref="ICurrentIdentity"/>), never from the request, and the tenant scope is already open
/// — so every read is filtered and an enrolment belonging to another Company is invisible rather
/// than merely forbidden.
/// <para>
/// <b>The secret is sealed before it is stored and never after.</b> It exists in the clear for the
/// length of this method and in the response, which is the only time an Employee can receive it.
/// </para>
/// </remarks>
public sealed class BeginMfaEnrollmentCommandHandler(
    ICurrentIdentity currentIdentity,
    IMfaEnrollmentRepository enrollments,
    IEnvelopeEncryptor encryptor,
    ITotpService totp,
    IEmployeeRepository employees,
    IUnitOfWork unitOfWork,
    IOptions<MfaOptions> options,
    TimeProvider timeProvider)
    : ICommandHandler<BeginMfaEnrollmentCommand, MfaEnrollmentSecret>
{
    public async Task<Result<MfaEnrollmentSecret>> HandleAsync(
        BeginMfaEnrollmentCommand command, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();
        var companyId = currentIdentity.RequireCompanyId();

        var existing = await enrollments.FindCurrentForAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is { IsActive: true })
        {
            // A live factor must be disabled deliberately, not replaced by starting again. Silently
            // superseding it would let anyone with a hijacked session swap the factor for one they
            // control without ever proving they hold the current one.
            return Result.Failure<MfaEnrollmentSecret>(
                Error.Conflict("Multi-factor authentication is already enabled."));
        }

        var employee = await employees.FindAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            return Result.Failure<MfaEnrollmentSecret>(Error.NotFound("No such Employee."));
        }

        if (existing is not null)
        {
            // An abandoned pending enrolment is discarded rather than reused: someone who scanned
            // the code into the wrong app must be able to start over, and the old secret must stop
            // being one that could confirm. It is removed rather than disabled because nothing was
            // ever in force, so there is no history to keep.
            //
            // Committed on its own, before the replacement is created. ux_mfa_enrollments_employee
            // _id_active is a partial unique index, and PostgreSQL cannot defer one — a single
            // batch would let the insert land while the old row is still live and fail the
            // constraint. The intermediate state is an Employee with no enrolment, which is
            // exactly what they had a moment earlier.
            enrollments.Remove(existing);

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var secret = totp.GenerateSecret();

        try
        {
            var enrollment = MfaEnrollment.Begin(
                companyId,
                employeeId,
                MfaMethod.Totp,
                encryptor.Protect(companyId, secret),
                timeProvider.GetUtcNow());

            enrollments.Add(enrollment);

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success(new MfaEnrollmentSecret(
                totp.Encode(secret),
                BuildUri(totp.Encode(secret), employee.Email.Value, options.Value.Issuer)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Builds the standard <c>otpauth://</c> Key Uri.
    /// </summary>
    /// <remarks>
    /// The format every authenticator app reads, and the reason no image is generated here: a
    /// client that wants a QR code renders this string locally, where the secret already is.
    /// Sending an image would put the secret through an encoder and into a response body that
    /// caches differently from text.
    /// </remarks>
    private static string BuildUri(string secret, string account, string issuer)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var escapedIssuer = Uri.EscapeDataString(issuer);

        return $"otpauth://totp/{label}?secret={secret}&issuer={escapedIssuer}";
    }
}

/// <summary>
/// Proves possession of the enrolled secret and issues recovery codes.
/// </summary>
/// <remarks>
/// One command, one commit: the enrolment becomes confirmed and its recovery codes appear
/// together. A confirmed factor with no codes is an Employee one lost phone away from a lost
/// account, and codes against an unconfirmed factor are a bypass of something not yet in force.
/// </remarks>
public sealed class ConfirmMfaEnrollmentCommandHandler(
    ICurrentIdentity currentIdentity,
    IMfaEnrollmentRepository enrollments,
    IMfaRecoveryCodeRepository codes,
    IEnvelopeEncryptor encryptor,
    ITotpService totp,
    IRecoveryCodeFactory recoveryCodes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<ConfirmMfaEnrollmentCommand, MfaRecoveryCodes>
{
    public async Task<Result<MfaRecoveryCodes>> HandleAsync(
        ConfirmMfaEnrollmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Code))
        {
            return Result.Failure<MfaRecoveryCodes>(Error.Validation("A code is required."));
        }

        var employeeId = currentIdentity.RequireEmployeeId();

        var enrollment = await enrollments.FindCurrentForAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (enrollment is null || !enrollment.IsPending)
        {
            return Result.Failure<MfaRecoveryCodes>(
                Error.Conflict("There is no enrolment awaiting confirmation."));
        }

        var now = timeProvider.GetUtcNow();
        var secret = encryptor.Unprotect(enrollment.CompanyId, enrollment.Secret);

        if (secret is null)
        {
            return Refused();
        }

        bool valid;

        try
        {
            valid = totp.IsValid(secret, command.Code, now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        // TryConfirm spends the step that proved possession, so the code just typed cannot be
        // turned around and replayed as a verification.
        if (!valid || !enrollment.TryConfirm(totp.TimeStepAt(now), now))
        {
            return Refused();
        }

        var issued = recoveryCodes.IssueSet();

        foreach (var code in issued)
        {
            codes.Add(MfaRecoveryCode.Issue(
                enrollment.CompanyId, employeeId, enrollment.Id, code.Hash, now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The only time these exist. Nothing stores the plaintext.
        return Result.Success(new MfaRecoveryCodes([.. issued.Select(code => code.Code)]));
    }

    private static Result<MfaRecoveryCodes> Refused() =>
        Result.Failure<MfaRecoveryCodes>(
            Error.AuthenticationFailed("The code is not valid."));
}

/// <summary>
/// Satisfies a second-factor challenge (§3.6 step-up).
/// </summary>
/// <remarks>
/// <b>Every failure is the same failure.</b> A wrong code, a replayed one, a reused recovery code,
/// and an enrolment that will not decrypt all return <c>authentication_failed</c> with one
/// description. Distinguishing them would tell whoever is guessing which attempts were close, and
/// "that code was right but already used" is the most useful thing an attacker could learn.
/// </remarks>
public sealed class VerifyMfaChallengeCommandHandler(
    ICurrentIdentity currentIdentity,
    IMfaEnrollmentRepository enrollments,
    IMfaRecoveryCodeRepository codes,
    MfaChallengeVerifier verifier,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<VerifyMfaChallengeCommand, MfaVerification>
{
    public async Task<Result<MfaVerification>> HandleAsync(
        VerifyMfaChallengeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Code))
        {
            return Result.Failure<MfaVerification>(Error.Validation("A code is required."));
        }

        var employeeId = currentIdentity.RequireEmployeeId();

        var enrollment = await enrollments.FindCurrentForAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (enrollment is null || !enrollment.IsActive)
        {
            // Not enrolled is a different situation from a wrong code — the Employee has nothing
            // to type — so it says so. It reveals nothing an authenticated caller does not already
            // know about their own account.
            return Result.Failure<MfaVerification>(
                Error.Conflict("Multi-factor authentication is not enabled."));
        }

        var now = timeProvider.GetUtcNow();

        var outcome = await verifier
            .VerifyAsync(enrollment, command.Code, now, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Satisfied)
        {
            // Committed anyway: a spent recovery code that failed for another reason must not stay
            // spendable, and the aggregate only marks what it actually consumed.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<MfaVerification>(
                Error.AuthenticationFailed("The code is not valid."));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var remaining = await codes.CountUnusedAsync(enrollment.Id, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new MfaVerification(outcome.UsedRecoveryCode, remaining));
    }
}

/// <summary>
/// Turns the second factor off, against a current code.
/// </summary>
/// <remarks>
/// The code is required for the reason §3.6 gives for step-up: this is the operation a hijacked
/// session would perform first, and re-proving possession is cheap relative to the consequence.
/// <para>
/// The recovery codes are deleted rather than tombstoned. A retained hash of a code that can no
/// longer be redeemed protects nothing and is one more copy of second-factor material; the
/// enrolment row stays, so when the factor was in force is still on record.
/// </para>
/// </remarks>
public sealed class DisableMfaCommandHandler(
    ICurrentIdentity currentIdentity,
    IMfaEnrollmentRepository enrollments,
    IMfaRecoveryCodeRepository codes,
    MfaChallengeVerifier verifier,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<DisableMfaCommand>
{
    public async Task<Result> HandleAsync(
        DisableMfaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Code))
        {
            return Result.Failure(Error.Validation("A code is required."));
        }

        var employeeId = currentIdentity.RequireEmployeeId();

        var enrollment = await enrollments.FindCurrentForAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (enrollment is null || !enrollment.IsActive)
        {
            return Result.Failure(
                Error.Conflict("Multi-factor authentication is not enabled."));
        }

        var now = timeProvider.GetUtcNow();

        var outcome = await verifier
            .VerifyAsync(enrollment, command.Code, now, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Satisfied)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure(Error.AuthenticationFailed("The code is not valid."));
        }

        enrollment.Disable(now);

        await codes.DeleteForEnrollmentAsync(enrollment.Id, cancellationToken)
            .ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
