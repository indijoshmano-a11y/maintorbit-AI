using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A Company's authentication policy.
/// </summary>
/// <remarks>
/// 02-authentication-architecture §3.10 states it plainly: "Company-level authentication policy
/// must exist at MVP even though only some methods do". Four requirements meet here — FR-AUTH-002
/// makes password strength configurable per Company, FR-AUTH-007 makes both session timers
/// Company-configured, FR-AUTH-011 makes the lockout threshold configurable, and FR-AUTH-006 lets
/// an administrator require a second factor.
/// <para>
/// <b>One row per Company, and a Company without one is not unconfigured.</b> Absence means the
/// deployment defaults apply, which is why nothing here is nullable and why
/// <see cref="Default"/> exists — a policy that could be half-set would make every reader ask
/// "and if this one is null?", and the answer would eventually differ between readers.
/// </para>
/// <para>
/// <b>Every setter returns a <see cref="Result"/> rather than throwing.</b> These values come from
/// an administrator filling in a form; a minimum length of zero is a mistake, not an exceptional
/// condition (EX-1).
/// </para>
/// </remarks>
public sealed class CompanyAuthenticationPolicy
{
    /// <summary>
    /// The shortest minimum length a Company may set.
    /// </summary>
    /// <remarks>
    /// compliance §14 argues that "a password that is long, unique, and not in a breach corpus is
    /// stronger than one that satisfies a character-class rule" — so length is the dial, and it has
    /// a floor. A Company may make its own policy stricter than the platform's; it may not make it
    /// weaker, or the setting becomes a way to opt out of the control.
    /// </remarks>
    public const int MinimumAllowedPasswordLength = 12;

    /// <summary>The longest minimum length that is still usable.</summary>
    /// <remarks>
    /// A ceiling because a policy nobody can satisfy is a Company that cannot onboard. The value
    /// is arbitrary in a way the floor is not, and is a guard rather than a security control.
    /// </remarks>
    public const int MaximumAllowedPasswordLength = 128;

    /// <summary>Bounds on the idle window, in minutes (§3.2).</summary>
    public const int MinimumIdleTimeoutMinutes = 5;

    /// <summary>Thirty days. Beyond this an idle timeout stops being one.</summary>
    public const int MaximumIdleTimeoutMinutes = 43_200;

    /// <summary>Bounds on the absolute lifetime, in minutes (§3.2).</summary>
    public const int MinimumAbsoluteLifetimeMinutes = 15;

    /// <summary>Thirty days.</summary>
    public const int MaximumAbsoluteLifetimeMinutes = 43_200;

    /// <summary>Bounds on the lockout threshold (FR-AUTH-011).</summary>
    public const int MinimumAllowedFailedAttempts = 3;

    /// <summary>
    /// Above this, lockout stops being a control.
    /// </summary>
    /// <remarks>
    /// A threshold of a thousand is a threshold an online guessing attack never reaches, and the
    /// setting would read as protection while providing none.
    /// </remarks>
    public const int MaximumAllowedFailedAttempts = 20;

    /// <summary>Bounds on how long a lockout lasts, in minutes.</summary>
    public const int MinimumLockoutMinutes = 1;

    /// <summary>One day.</summary>
    public const int MaximumLockoutMinutes = 1_440;

    /// <summary>Constructor for the persistence layer.</summary>
    private CompanyAuthenticationPolicy()
    {
    }

    private CompanyAuthenticationPolicy(
        CompanyId companyId,
        int minimumPasswordLength,
        bool requireBreachCheck,
        int idleTimeoutMinutes,
        int absoluteLifetimeMinutes,
        bool mfaRequired,
        int maximumFailedAttempts,
        int lockoutMinutes,
        DateTimeOffset createdAtUtc)
    {
        CompanyId = companyId;
        MinimumPasswordLength = minimumPasswordLength;
        RequireBreachCheck = requireBreachCheck;
        IdleTimeoutMinutes = idleTimeoutMinutes;
        AbsoluteLifetimeMinutes = absoluteLifetimeMinutes;
        MfaRequired = mfaRequired;
        MaximumFailedAttempts = maximumFailedAttempts;
        LockoutMinutes = lockoutMinutes;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// The Company this governs — the identity and the tenant discriminator at once.
    /// </summary>
    /// <remarks>
    /// The primary key, not a foreign key beside a surrogate one. There is exactly one policy per
    /// Company, and a table with a separate identifier would permit two — which is a state nothing
    /// could resolve, because neither would be more current than the other.
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>Shortest password the Company accepts (FR-AUTH-002).</summary>
    public int MinimumPasswordLength { get; private set; }

    /// <summary>
    /// Whether a new password is checked against known-compromised credential lists.
    /// </summary>
    /// <remarks>
    /// FR-AUTH-002 and compliance §14, which calls this "more valuable than complexity rules". The
    /// flag is stored and readable; the corpus itself is not built, so nothing consults it yet —
    /// see the milestone's deferred work rather than assuming the check runs.
    /// </remarks>
    public bool RequireBreachCheck { get; private set; }

    /// <summary>How long a session may sit unused before it ends (FR-AUTH-007).</summary>
    public int IdleTimeoutMinutes { get; private set; }

    /// <summary>How long a session may live regardless of activity (FR-AUTH-007).</summary>
    public int AbsoluteLifetimeMinutes { get; private set; }

    /// <summary>
    /// Whether every Employee must hold a second factor (FR-AUTH-006).
    /// </summary>
    /// <remarks>
    /// A flag, not a per-role rule. FR-AUTH-006 allows "all Employees or specified roles"; the
    /// role-specific half needs a set of role codes on this row and a resolution against the
    /// Employee's assignments, which is more than a flag and is not what this milestone builds.
    /// </remarks>
    public bool MfaRequired { get; private set; }

    /// <summary>Consecutive failures before an account locks (FR-AUTH-011).</summary>
    public int MaximumFailedAttempts { get; private set; }

    /// <summary>How long a lockout lasts, in minutes.</summary>
    public int LockoutMinutes { get; private set; }

    /// <summary>Row creation (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Who last changed it.</summary>
    public EmployeeId? UpdatedByEmployeeId { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>
    /// The policy a Company has until it sets one.
    /// </summary>
    /// <remarks>
    /// Not persisted — it is what a reader gets when no row exists, so that "unconfigured" and
    /// "configured to the defaults" behave identically. The numbers match the deployment options
    /// they replace, and a startup check asserts they still do: two sets of defaults that drift
    /// would make behaviour depend on whether a Company had ever opened the settings page.
    /// </remarks>
    public static CompanyAuthenticationPolicy Default(CompanyId companyId) =>
        new(companyId,
            minimumPasswordLength: MinimumAllowedPasswordLength,
            requireBreachCheck: true,
            idleTimeoutMinutes: 60,
            absoluteLifetimeMinutes: 720,
            mfaRequired: false,
            maximumFailedAttempts: 5,
            lockoutMinutes: 15,
            createdAtUtc: default);

    /// <summary>
    /// Creates a Company's policy from administrator-supplied values.
    /// </summary>
    /// <remarks>
    /// Every bound is checked here rather than at the endpoint. The endpoint validates shape — a
    /// field is present, a number is a number — and the aggregate validates meaning, which is the
    /// half that must hold however the row is written.
    /// </remarks>
    public static Result<CompanyAuthenticationPolicy> Create(
        CompanyId companyId,
        int minimumPasswordLength,
        bool requireBreachCheck,
        int idleTimeoutMinutes,
        int absoluteLifetimeMinutes,
        bool mfaRequired,
        int maximumFailedAttempts,
        int lockoutMinutes,
        DateTimeOffset createdAtUtc)
    {
        if (companyId.IsEmpty)
        {
            return Result.Failure<CompanyAuthenticationPolicy>(
                Error.Validation("A policy must belong to a Company."));
        }

        if (Validate(
                minimumPasswordLength,
                idleTimeoutMinutes,
                absoluteLifetimeMinutes,
                maximumFailedAttempts,
                lockoutMinutes) is { } invalid)
        {
            return Result.Failure<CompanyAuthenticationPolicy>(invalid);
        }

        return Result.Success(new CompanyAuthenticationPolicy(
            companyId,
            minimumPasswordLength,
            requireBreachCheck,
            idleTimeoutMinutes,
            absoluteLifetimeMinutes,
            mfaRequired,
            maximumFailedAttempts,
            lockoutMinutes,
            createdAtUtc));
    }

    /// <summary>Replaces every setting at once.</summary>
    /// <remarks>
    /// Whole-policy replacement rather than a setter per field, because the rules are relational:
    /// the absolute lifetime must not be shorter than the idle window, and checking that on each
    /// individual change would refuse a legitimate pair depending on which half arrived first.
    /// </remarks>
    public Result Update(
        int minimumPasswordLength,
        bool requireBreachCheck,
        int idleTimeoutMinutes,
        int absoluteLifetimeMinutes,
        bool mfaRequired,
        int maximumFailedAttempts,
        int lockoutMinutes,
        DateTimeOffset updatedAtUtc,
        EmployeeId? updatedBy = null)
    {
        if (Validate(
                minimumPasswordLength,
                idleTimeoutMinutes,
                absoluteLifetimeMinutes,
                maximumFailedAttempts,
                lockoutMinutes) is { } invalid)
        {
            return Result.Failure(invalid);
        }

        MinimumPasswordLength = minimumPasswordLength;
        RequireBreachCheck = requireBreachCheck;
        IdleTimeoutMinutes = idleTimeoutMinutes;
        AbsoluteLifetimeMinutes = absoluteLifetimeMinutes;
        MfaRequired = mfaRequired;
        MaximumFailedAttempts = maximumFailedAttempts;
        LockoutMinutes = lockoutMinutes;
        UpdatedAtUtc = updatedAtUtc;
        UpdatedByEmployeeId = updatedBy;

        return Result.Success();
    }

    /// <summary>Whether a candidate password satisfies the length policy.</summary>
    /// <remarks>
    /// Length only, and deliberately. compliance §14 prefers breach-corpus checking to
    /// character-class rules, so the aggregate exposes the rule it can decide and leaves the
    /// corpus lookup to a port that does not exist yet.
    /// </remarks>
    public bool IsPasswordLongEnough(int length) => length >= MinimumPasswordLength;

    /// <summary>
    /// The single place every bound is stated.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="Create"/> and <see cref="Update"/>, because a bound enforced on
    /// creation and not on update is a bound an administrator can step around by saving twice.
    /// </remarks>
    private static Error? Validate(
        int minimumPasswordLength,
        int idleTimeoutMinutes,
        int absoluteLifetimeMinutes,
        int maximumFailedAttempts,
        int lockoutMinutes)
    {
        if (minimumPasswordLength is < MinimumAllowedPasswordLength or > MaximumAllowedPasswordLength)
        {
            return Error.Validation(
                $"The minimum password length must be between {MinimumAllowedPasswordLength} " +
                $"and {MaximumAllowedPasswordLength}.");
        }

        if (idleTimeoutMinutes is < MinimumIdleTimeoutMinutes or > MaximumIdleTimeoutMinutes)
        {
            return Error.Validation(
                $"The idle timeout must be between {MinimumIdleTimeoutMinutes} and " +
                $"{MaximumIdleTimeoutMinutes} minutes.");
        }

        if (absoluteLifetimeMinutes is < MinimumAbsoluteLifetimeMinutes
            or > MaximumAbsoluteLifetimeMinutes)
        {
            return Error.Validation(
                $"The absolute lifetime must be between {MinimumAbsoluteLifetimeMinutes} and " +
                $"{MaximumAbsoluteLifetimeMinutes} minutes.");
        }

        if (absoluteLifetimeMinutes < idleTimeoutMinutes)
        {
            // §3.2 calls the absolute lifetime "the one that cannot be defeated by activity". An
            // absolute lifetime shorter than the idle window makes the idle window unreachable —
            // the session always ends first, and the setting reads as configured and does nothing.
            return Error.Validation(
                "The absolute lifetime must not be shorter than the idle timeout.");
        }

        if (maximumFailedAttempts is < MinimumAllowedFailedAttempts or > MaximumAllowedFailedAttempts)
        {
            return Error.Validation(
                $"The lockout threshold must be between {MinimumAllowedFailedAttempts} and " +
                $"{MaximumAllowedFailedAttempts} failed attempts.");
        }

        if (lockoutMinutes is < MinimumLockoutMinutes or > MaximumLockoutMinutes)
        {
            return Error.Validation(
                $"The lockout duration must be between {MinimumLockoutMinutes} and " +
                $"{MaximumLockoutMinutes} minutes.");
        }

        return null;
    }
}
