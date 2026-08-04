using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A single-use recovery code — C4 data.
/// </summary>
/// <remarks>
/// 02-authentication-architecture §3.6: recovery codes are "issued once, hashed at rest,
/// single-use". They exist because a lost authenticator would otherwise be a lost account, and
/// they are the only way past the second factor — which makes each one as sensitive as the TOTP
/// secret itself, and is why only the digest is stored.
/// <para>
/// <b>Used codes are not deleted.</b> Recognising a spent code is what makes reuse refusable; a
/// deleted row turns a second presentation into "unknown code", which is the same answer as a
/// typo and tells nobody that a code was used twice.
/// </para>
/// </remarks>
public sealed class MfaRecoveryCode
{
    /// <summary>Constructor for the persistence layer.</summary>
    private MfaRecoveryCode() => CodeHash = null!;

    private MfaRecoveryCode(
        MfaRecoveryCodeId id,
        CompanyId companyId,
        EmployeeId employeeId,
        MfaEnrollmentId enrollmentId,
        RecoveryCodeHash codeHash,
        DateTimeOffset issuedAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        EnrollmentId = enrollmentId;
        CodeHash = codeHash;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Identifier of this code's record.</summary>
    public MfaRecoveryCodeId Id { get; private init; }

    /// <summary>The Company this code belongs to — the tenant discriminator (DB-P1).</summary>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee this code recovers.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>
    /// The enrolment this set was issued alongside.
    /// </summary>
    /// <remarks>
    /// Codes belong to an enrolment, not merely to an Employee. Disabling a factor and enrolling
    /// again must not leave the previous set usable — a recovery code that outlives the factor it
    /// recovers is a permanent bypass.
    /// </remarks>
    public MfaEnrollmentId EnrollmentId { get; private init; }

    /// <summary>SHA-256 of the code. The code itself is never stored.</summary>
    public RecoveryCodeHash CodeHash { get; private init; }

    /// <summary>When the code was issued (§1.7).</summary>
    public DateTimeOffset IssuedAtUtc { get; private init; }

    /// <summary>
    /// When the code was spent, or <see langword="null"/> if it has not been.
    /// </summary>
    /// <remarks>
    /// The single-use half. A code presented with this already set is being reused.
    /// </remarks>
    public DateTimeOffset? UsedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the code has already been spent.</summary>
    public bool IsUsed => UsedAtUtc is not null;

    /// <summary>
    /// Issues one code of a set.
    /// </summary>
    /// <remarks>
    /// Takes an already-computed hash. Nothing here hashes, and nothing here accepts the code
    /// itself — the set exists in plaintext exactly once, on its way to the Employee.
    /// </remarks>
    /// <exception cref="ArgumentException">The Company, Employee, or enrolment identifier is
    /// unset.</exception>
    public static MfaRecoveryCode Issue(
        CompanyId companyId,
        EmployeeId employeeId,
        MfaEnrollmentId enrollmentId,
        RecoveryCodeHash codeHash,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(codeHash);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException(
                "A recovery code must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            throw new ArgumentException(
                "A recovery code must belong to an Employee.", nameof(employeeId));
        }

        if (enrollmentId.IsEmpty)
        {
            // Unbound, it would survive the enrolment it recovers and become a permanent bypass.
            throw new ArgumentException(
                "A recovery code must belong to an enrolment.", nameof(enrollmentId));
        }

        return new MfaRecoveryCode(
            MfaRecoveryCodeId.New(), companyId, employeeId, enrollmentId, codeHash, issuedAtUtc);
    }

    /// <summary>
    /// Spends the code.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if it was already used — the caller must treat that as
    /// reuse and refuse, not retry. Deciding here rather than by reading <see cref="UsedAtUtc"/>
    /// means the decision cannot be made against a value that has since changed.
    /// </remarks>
    public bool TryConsume(DateTimeOffset usedAtUtc)
    {
        if (IsUsed)
        {
            return false;
        }

        UsedAtUtc = usedAtUtc;
        return true;
    }
}
