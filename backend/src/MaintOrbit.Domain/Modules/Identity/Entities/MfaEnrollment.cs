using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// An Employee's TOTP second factor — C4 data.
/// </summary>
/// <remarks>
/// FR-AUTH-005 makes TOTP an MVP capability, and 02-authentication-architecture §3.6 is explicit
/// that it is "an MVP capability, not merely MFA-ready". The shared secret is the whole factor:
/// anyone holding it can produce codes indefinitely, so §4.2 stores it "encrypted under the
/// Company DEK using the same envelope scheme as Provider Credentials" and this aggregate never
/// holds it in the clear.
/// <para>
/// <b>Two properties carry the security weight.</b> <see cref="Status"/> keeps an unproved secret
/// from becoming a factor that can lock its owner out, and
/// <see cref="LastAcceptedTimeStep"/> implements §3.6's "a used TOTP code is rejected within its
/// window" — without it, a code observed over the shoulder or captured in transit stays valid for
/// the remainder of its step.
/// </para>
/// <para>
/// <b>One live enrolment per Employee.</b> Disabled rows are retained rather than deleted, so the
/// history of when a factor was turned on and off survives; a partial unique index enforces that
/// only one is not disabled.
/// </para>
/// </remarks>
public sealed class MfaEnrollment
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so an enrolment can only come from <see cref="Begin"/>. EF materializes through it,
    /// which correctly bypasses the invariants — a stored row satisfied them when it was written.
    /// </remarks>
    private MfaEnrollment() => Secret = null!;

    private MfaEnrollment(
        MfaEnrollmentId id,
        CompanyId companyId,
        EmployeeId employeeId,
        MfaMethod method,
        SecretEnvelope secret,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        Method = method;
        Secret = secret;
        Status = MfaEnrollmentStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Identifier.</summary>
    public MfaEnrollmentId Id { get; private init; }

    /// <summary>
    /// The Company this enrolment belongs to — the tenant discriminator (DB-P1).
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than reached through the Employee, so the row-level security
    /// policy compares against a local column instead of joining to <c>employees</c> per row.
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee this factor belongs to.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>Which kind of second factor. TOTP is the only one at MVP.</summary>
    public MfaMethod Method { get; private init; }

    /// <summary>The TOTP shared secret, encrypted (§4.2). Never held in the clear here.</summary>
    public SecretEnvelope Secret { get; private init; }

    /// <summary>Where the enrolment is in its lifecycle.</summary>
    public MfaEnrollmentStatus Status { get; private set; }

    /// <summary>
    /// The most recent TOTP time step accepted for this enrolment.
    /// </summary>
    /// <remarks>
    /// §3.6: "A used TOTP code is rejected within its window." A TOTP code is a function of the
    /// secret and the step, so a code is exactly a step — recording the last one accepted and
    /// refusing anything not strictly later makes replay impossible without storing codes.
    /// <para>
    /// Null until the first acceptance, which is the confirmation itself: the code the Employee
    /// returns to prove possession is also the first one spent.
    /// </para>
    /// </remarks>
    public long? LastAcceptedTimeStep { get; private set; }

    /// <summary>Row creation (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>When possession was proved, or <see langword="null"/> if it has not been.</summary>
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    /// <summary>When the factor was last used successfully.</summary>
    public DateTimeOffset? LastVerifiedAtUtc { get; private set; }

    /// <summary>When the factor was turned off, or <see langword="null"/> if it is still live.</summary>
    public DateTimeOffset? DisabledAtUtc { get; private set; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the factor is confirmed and not disabled.</summary>
    public bool IsActive =>
        Status == MfaEnrollmentStatus.Confirmed && DisabledAtUtc is null;

    /// <summary>Whether a secret has been issued but possession is unproved.</summary>
    public bool IsPending =>
        Status == MfaEnrollmentStatus.Pending && DisabledAtUtc is null;

    /// <summary>
    /// Begins an enrolment by recording an encrypted secret.
    /// </summary>
    /// <remarks>
    /// Takes an already-sealed envelope. Nothing here encrypts, and nothing here accepts a
    /// plaintext secret — a domain type that could hold one is a domain type that can log one, and
    /// the Domain project carries no cryptographic package at all (ADR-0001, enforced by an
    /// architecture test).
    /// </remarks>
    /// <exception cref="ArgumentException">The Company or Employee identifier is unset.</exception>
    public static MfaEnrollment Begin(
        CompanyId companyId,
        EmployeeId employeeId,
        MfaMethod method,
        SecretEnvelope secret,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException(
                "An enrolment must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            // An enrolment bound to nobody authenticates nobody, but it would still be a live
            // shared secret sitting in the most sensitive table in the schema.
            throw new ArgumentException(
                "An enrolment must belong to an Employee.", nameof(employeeId));
        }

        return new MfaEnrollment(
            MfaEnrollmentId.New(), companyId, employeeId, method, secret, createdAtUtc);
    }

    /// <summary>
    /// Confirms the enrolment against the first accepted time step.
    /// </summary>
    /// <remarks>
    /// The caller has already checked the code against the secret; this records the outcome and
    /// spends the step, so the very code that proved possession cannot then be replayed as a
    /// verification. Returns <see langword="false"/> rather than throwing when the enrolment is
    /// not pending — a second confirmation is an ordinary duplicate request, not a fault.
    /// </remarks>
    public bool TryConfirm(long timeStep, DateTimeOffset confirmedAtUtc)
    {
        if (!IsPending)
        {
            return false;
        }

        Status = MfaEnrollmentStatus.Confirmed;
        ConfirmedAtUtc = confirmedAtUtc;
        LastVerifiedAtUtc = confirmedAtUtc;
        LastAcceptedTimeStep = timeStep;
        UpdatedAtUtc = confirmedAtUtc;

        return true;
    }

    /// <summary>
    /// Whether a time step may still be spent against this enrolment.
    /// </summary>
    /// <remarks>
    /// Strictly greater, not greater-or-equal. Equal means the same code is being presented twice,
    /// which is the replay §3.6 requires refusing; earlier means a code from a past step, which is
    /// the same replay with a delay.
    /// </remarks>
    public bool IsUnusedTimeStep(long timeStep) =>
        LastAcceptedTimeStep is not { } last || timeStep > last;

    /// <summary>
    /// Spends a time step against a confirmed enrolment.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a replayed or stale step and for an enrolment that is
    /// not active. Deciding here rather than by reading <see cref="LastAcceptedTimeStep"/> at the
    /// call site means the decision cannot be made against a value that has since moved.
    /// </remarks>
    public bool TryAcceptTimeStep(long timeStep, DateTimeOffset verifiedAtUtc)
    {
        if (!IsActive || !IsUnusedTimeStep(timeStep))
        {
            return false;
        }

        LastAcceptedTimeStep = timeStep;
        LastVerifiedAtUtc = verifiedAtUtc;
        UpdatedAtUtc = verifiedAtUtc;

        return true;
    }

    /// <summary>
    /// Records that the factor was satisfied by a recovery code rather than a TOTP code.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryAcceptTimeStep"/> because no step is spent: a recovery code is
    /// its own single-use credential, and advancing the step counter would invalidate the
    /// authenticator's current code as a side effect of not having used it.
    /// </remarks>
    public void RecordRecovery(DateTimeOffset verifiedAtUtc)
    {
        LastVerifiedAtUtc = verifiedAtUtc;
        UpdatedAtUtc = verifiedAtUtc;
    }

    /// <summary>
    /// Turns the factor off.
    /// </summary>
    /// <remarks>
    /// Idempotent, and the row stays. Deleting it would erase when a second factor was in force,
    /// which is exactly what an investigation needs afterwards — and would let the same secret be
    /// re-enrolled without anything recording that it had been abandoned.
    /// </remarks>
    public void Disable(DateTimeOffset disabledAtUtc)
    {
        if (DisabledAtUtc is not null)
        {
            return;
        }

        Status = MfaEnrollmentStatus.Disabled;
        DisabledAtUtc = disabledAtUtc;
        UpdatedAtUtc = disabledAtUtc;
    }
}
