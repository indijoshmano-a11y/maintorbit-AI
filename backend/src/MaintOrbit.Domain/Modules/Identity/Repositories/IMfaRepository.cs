using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>Access to <see cref="MfaEnrollment"/> aggregates.</summary>
/// <remarks>
/// Tenant filtering is absent for the same reason as every other repository here: row-level
/// security applies it below the application layer (ADR-0005), and a second discretionary copy is
/// the one that gets forgotten.
/// </remarks>
public interface IMfaEnrollmentRepository
{
    /// <summary>
    /// The Employee's live enrolment — confirmed or awaiting confirmation — or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// One question, not two, because every caller asks it the same way: enrolment must refuse to
    /// start a second one, confirmation needs the pending one, and verification needs the
    /// confirmed one. Disabled rows are excluded — they are history, and a disabled factor must
    /// never satisfy a challenge.
    /// </remarks>
    Task<MfaEnrollment?> FindCurrentForAsync(
        EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>Adds a new enrolment to the unit of work.</summary>
    void Add(MfaEnrollment enrollment);

    /// <summary>
    /// Removes an enrolment that was never confirmed.
    /// </summary>
    /// <remarks>
    /// Only for a pending one. A confirmed factor is disabled and kept, because when it was in
    /// force is exactly what an investigation needs afterwards — but an enrolment nobody completed
    /// records no event at all, and keeping it would put a row in that history for something that
    /// never happened.
    /// </remarks>
    void Remove(MfaEnrollment enrollment);
}

/// <summary>Access to <see cref="MfaRecoveryCode"/> aggregates.</summary>
public interface IMfaRecoveryCodeRepository
{
    /// <summary>
    /// Finds an unspent recovery code by its digest, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Scoped to an enrolment so a set cannot outlive the factor it belongs to. Used rows are
    /// returned too — recognising a spent code is what makes reuse refusable, and filtering here
    /// would turn a second presentation into "unknown code", which is the same answer as a typo.
    /// </remarks>
    Task<MfaRecoveryCode?> FindByHashAsync(
        MfaEnrollmentId enrollmentId, RecoveryCodeHash hash, CancellationToken cancellationToken);

    /// <summary>How many codes an enrolment still has unspent.</summary>
    /// <remarks>
    /// Returned to the Employee after a recovery so they know how close they are to having none
    /// left — running out silently is how a lost authenticator becomes a lost account.
    /// </remarks>
    Task<int> CountUnusedAsync(MfaEnrollmentId enrollmentId, CancellationToken cancellationToken);

    /// <summary>Adds a new recovery code to the unit of work.</summary>
    void Add(MfaRecoveryCode code);

    /// <summary>
    /// Deletes every code belonging to an enrolment.
    /// </summary>
    /// <remarks>
    /// Called when a factor is disabled. This is the one place a C4 row is removed rather than
    /// tombstoned, and deliberately: a retained hash of a code that can no longer be redeemed
    /// protects nothing and is one more copy of second-factor material to defend. The enrolment
    /// row survives, so when the factor was in force is still on record.
    /// </remarks>
    Task<int> DeleteForEnrollmentAsync(
        MfaEnrollmentId enrollmentId, CancellationToken cancellationToken);
}
