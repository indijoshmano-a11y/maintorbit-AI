using System.Security.Cryptography;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;

namespace MaintOrbit.Application.Modules.Identity.Commands.Mfa;

/// <summary>
/// Checks a presented code against an enrolment — the one place that decides.
/// </summary>
/// <remarks>
/// Shared by verification and by disabling, because both ask the same question and answering it
/// twice is how the two answers drift. It mutates the aggregates it accepts — spending a time step
/// or a recovery code is part of deciding, not a consequence of it — and leaves committing to the
/// caller.
/// </remarks>
public sealed class MfaChallengeVerifier(
    IEnvelopeEncryptor encryptor,
    ITotpService totp,
    IRecoveryCodeFactory recoveryCodes,
    IMfaRecoveryCodeRepository codes)
{
    /// <summary>What a challenge attempt resolved to.</summary>
    /// <param name="Satisfied">Whether the factor was proved.</param>
    /// <param name="UsedRecoveryCode">Whether a recovery code was spent to do it.</param>
    public readonly record struct Outcome(bool Satisfied, bool UsedRecoveryCode)
    {
        /// <summary>A refusal — for a wrong code, a replay, a reused recovery code, or a
        /// secret that will not open.</summary>
        public static Outcome Refused => default;
    }

    /// <summary>
    /// Checks a code, spending whatever it consumes.
    /// </summary>
    /// <remarks>
    /// <b>A replayed TOTP code does not fall through to the recovery lookup.</b> Once the code is
    /// recognised as this secret's output, the only remaining question is whether its step is
    /// already spent — and if it is, §3.6 requires refusing. Continuing on would let a replayed
    /// code be re-tested as a recovery code, which is a second guess bought with a stale one.
    /// </remarks>
    public async Task<Outcome> VerifyAsync(
        MfaEnrollment enrollment,
        string presentedCode,
        DateTimeOffset asAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);

        var secret = encryptor.Unprotect(enrollment.CompanyId, enrollment.Secret);

        if (secret is null)
        {
            // The envelope did not authenticate: tampering, or a DEK version this deployment
            // cannot open. Neither is the Employee's doing and neither is recoverable here, so it
            // refuses like any other failed attempt rather than reporting a fault they cannot act
            // on.
            return Outcome.Refused;
        }

        try
        {
            if (totp.IsValid(secret, presentedCode, asAt))
            {
                // TryAcceptTimeStep is the replay gate. It refuses a step already spent, which is
                // what "a used TOTP code is rejected within its window" means in practice.
                return new Outcome(
                    enrollment.TryAcceptTimeStep(totp.TimeStepAt(asAt), asAt),
                    UsedRecoveryCode: false);
            }
        }
        finally
        {
            // The decrypted secret exists for as long as one comparison takes and no longer. A
            // heap that still holds it is a memory dump that still holds it.
            CryptographicOperations.ZeroMemory(secret);
        }

        return await TryRecoveryCodeAsync(enrollment, presentedCode, asAt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Outcome> TryRecoveryCodeAsync(
        MfaEnrollment enrollment,
        string presentedCode,
        DateTimeOffset asAt,
        CancellationToken cancellationToken)
    {
        if (!TryHash(presentedCode, out var hash))
        {
            return Outcome.Refused;
        }

        var code = await codes.FindByHashAsync(enrollment.Id, hash, cancellationToken)
            .ConfigureAwait(false);

        // Scoped to this enrolment, so a set issued for a factor that was disabled and replaced
        // cannot satisfy the new one.
        if (code is null || !code.TryConsume(asAt))
        {
            return Outcome.Refused;
        }

        // No time step is spent. A recovery code is its own credential, and advancing the counter
        // would invalidate the authenticator's current code as a side effect of not using it.
        enrollment.RecordRecovery(asAt);

        return new Outcome(Satisfied: true, UsedRecoveryCode: true);
    }

    /// <summary>Hashes a candidate, treating an unhashable one as simply not matching.</summary>
    /// <remarks>
    /// A six-digit TOTP code reaches here whenever it was wrong, and it is not a recovery code.
    /// Letting the factory throw would turn "wrong code" into an exception on the ordinary failure
    /// path — observable in logs and in timing, and different from the failure a real but unknown
    /// recovery code produces.
    /// </remarks>
    private bool TryHash(
        string presentedCode, out Domain.Modules.Identity.ValueObjects.RecoveryCodeHash hash)
    {
        try
        {
            hash = recoveryCodes.Hash(presentedCode);
            return true;
        }
        catch (ArgumentException)
        {
            hash = null!;
            return false;
        }
    }
}
