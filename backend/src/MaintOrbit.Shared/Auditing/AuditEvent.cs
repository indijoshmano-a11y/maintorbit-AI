namespace MaintOrbit.Shared.Auditing;

/// <summary>Who performed an action.</summary>
public enum AuditActorType
{
    /// <summary>Nobody authenticated — a failed sign-in, an unredeemable link.</summary>
    Anonymous = 0,

    /// <summary>An Employee, identified by the credential they presented.</summary>
    Employee = 1,

    /// <summary>The platform itself — a scheduled sweep, a cascade.</summary>
    System = 2
}

/// <summary>How an action ended.</summary>
/// <remarks>
/// <see cref="Denied"/> is separate from <see cref="Failure"/> on purpose. §3.4 calls
/// authorization denials "a primary detection signal — a burst from one identity is a
/// privilege-escalation attempt in progress", and a denial mixed in with ordinary failures is one
/// nobody can alert on.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>The action was performed.</summary>
    Success = 0,

    /// <summary>The action was attempted and did not succeed.</summary>
    Failure = 1,

    /// <summary>The actor was not permitted (FR-PERM-004).</summary>
    Denied = 2
}

/// <summary>
/// One audit record, as the identity module emits it.
/// </summary>
/// <remarks>
/// A <b>published contract</b>, which is why it lives in Shared —
/// backend-architecture-overview lists "published contracts" among Shared's contents, and
/// ADR-0002 permits a module to reference another's published contracts and nothing else. The
/// <c>auditing</c> module consumes this; it does not expose its store, and identity does not reach
/// for one.
/// <para>
/// The fields are §4.2's <c>audit_events</c> columns and AU-3's requirement — "actor, action,
/// target, outcome, timestamp, originating context". Primitives and identifiers only, per
/// backend-architecture-overview's rule that integration events "carry identifiers and primitives
/// only": a domain type here would make the contract a dependency on identity's internals.
/// </para>
/// <para>
/// <b><see cref="Context"/> never carries content.</b> AU-4 forbids prompt or completion content in
/// an audit record, and §5 lists "content leaking into audit records" as a risk mitigated by it
/// "never being a plain string type". This map is for small facts — a client type, a revocation
/// reason, a role code — and nothing that was typed by a person.
/// </para>
/// </remarks>
/// <param name="OccurredAtUtc">When it happened (§1.7, UTC).</param>
/// <param name="Action">What was attempted — one of <see cref="AuditActions"/>.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ActorType">What kind of actor.</param>
/// <param name="CompanyId">The tenant, when one was established.</param>
/// <param name="ActorEmployeeId">The Employee, when one was identified.</param>
/// <param name="TargetType">What was acted on — one of <see cref="AuditTargets"/>.</param>
/// <param name="TargetId">Which one, when it has an identifier.</param>
/// <param name="CorrelationId">The request this belongs to, so a support conversation can find it.</param>
/// <param name="Context">Small non-content facts.</param>
public sealed record AuditEvent(
    DateTimeOffset OccurredAtUtc,
    string Action,
    AuditOutcome Outcome,
    AuditActorType ActorType,
    Guid? CompanyId = null,
    Guid? ActorEmployeeId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Context = null);

/// <summary>
/// The action names this module emits.
/// </summary>
/// <remarks>
/// Constants rather than literals at each call site, so an action is spelled one way and a search
/// for "every sign-in failure" finds all of them. §3.4 groups them; the names follow
/// <c>category.action</c>, matching the permission codes' shape.
/// </remarks>
public static class AuditActions
{
    /// <summary>A sign-in attempt (FR-AUTH-014).</summary>
    public const string SignIn = "authentication.sign-in";

    /// <summary>A sign-out of one device session.</summary>
    public const string SignOut = "authentication.sign-out";

    /// <summary>A sign-out of every device session.</summary>
    public const string SignOutEverywhere = "authentication.sign-out-all";

    /// <summary>An account locked after repeated failures (FR-AUTH-011).</summary>
    public const string AccountLockout = "authentication.lockout";

    /// <summary>A second factor was enrolled, but not yet proved (FR-AUTH-005).</summary>
    public const string MfaEnrollmentBegun = "authentication.mfa.enrol";

    /// <summary>A second factor was confirmed and is now in force.</summary>
    public const string MfaEnrollmentConfirmed = "authentication.mfa.confirm";

    /// <summary>A second-factor challenge was answered (§3.4's "MFA challenge").</summary>
    public const string MfaChallenge = "authentication.mfa.challenge";

    /// <summary>A second factor was turned off.</summary>
    public const string MfaDisabled = "authentication.mfa.disable";

    /// <summary>A session was terminated by the Employee holding it (FR-AUTH-008).</summary>
    public const string SessionRevoked = "session.revoke";

    /// <summary>Every session but the current one was terminated.</summary>
    public const string OtherSessionsRevoked = "session.revoke-others";

    /// <summary>A role was granted to an Employee (FR-TEN-*, §3.4 "role changes").</summary>
    public const string RoleAssigned = "employee.role.assign";

    /// <summary>A role was taken away.</summary>
    public const string RoleRemoved = "employee.role.remove";

    /// <summary>An authorization denial (FR-PERM-004).</summary>
    public const string PermissionDenied = "authorization.denied";

    /// <summary>
    /// An audit export was performed.
    /// </summary>
    /// <remarks>
    /// AC-i and §3.6 both require it — "export is itself an audited event, including actor, scope,
    /// and destination" — because bulk data leaving is a security-relevant act, and §3.5 lists
    /// export among the events an exfiltration investigation looks for.
    /// <para>
    /// <b>The name is new; the requirement is not.</b> Nothing documents what this action is
    /// called, so it follows the <c>category.verb</c> form §3.4 ratified in 12.2 and the resource
    /// name the permission already uses. Recorded as an assumption in the milestone report.
    /// </para>
    /// <para>
    /// <b>Scope, not destination.</b> The context carries the filter that selected the rows and
    /// how many were written. There is no destination to record: the export streams to the caller
    /// over the same authenticated request, and the "destination" §3.6 anticipates belongs to
    /// FR-AUD-009 continuous streaming, which is v1.1.
    /// </para>
    /// </remarks>
    public const string AuditExported = "audit.export";
}

/// <summary>The target kinds this module names.</summary>
public static class AuditTargets
{
    /// <summary>An Employee.</summary>
    public const string Employee = "employee";

    /// <summary>A device-scoped session.</summary>
    public const string Session = "session";

    /// <summary>A multi-factor enrolment.</summary>
    public const string MfaEnrollment = "mfa-enrollment";

    /// <summary>A role assignment.</summary>
    public const string RoleAssignment = "role-assignment";

    /// <summary>An API endpoint, for a denial that never reached a resource.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>The audit trail itself, as the target of an export.</summary>
    public const string AuditTrail = "audit-trail";
}
