using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// A user account belonging to exactly one Company.
/// </summary>
/// <remarks>
/// The aggregate root of the identity module. Named <c>Employee</c> because the glossary is
/// normative and §10 prohibits "User" — it is ambiguous between a human, a Platform API Key, and
/// a service identity, all three of which authenticate here.
/// <para>
/// Credentials, sessions, federated identities, and MFA enrolments are separate tables
/// (§4.2) and separate aggregates: they have independent lifetimes, and
/// <c>employee_credentials</c> is C4 data that must not be loaded alongside an ordinary
/// Employee read. This aggregate holds identity and lifecycle only.
/// </para>
/// <para>
/// <b>No timestamp is read here.</b> U-3 forbids the ambient clock outside the time abstraction
/// and AT-9 enforces it, so every point in time is supplied by the caller. That is also what
/// makes lifecycle rules testable without waiting.
/// </para>
/// </remarks>
public sealed class Employee
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so a valid Employee can only come from <see cref="Invite"/>. EF Core materializes
    /// through it by convention, which lets rehydration bypass the invariants — correctly, since
    /// a stored row already satisfied them when it was written.
    /// </remarks>
    private Employee()
    {
        Email = null!;
    }

    private Employee(
        EmployeeId id,
        CompanyId companyId,
        Email email,
        DateTimeOffset invitedAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        Email = email;
        Status = EmployeeStatus.Invited;
        CreatedAtUtc = invitedAtUtc;
        UpdatedAtUtc = invitedAtUtc;
    }

    /// <summary>Identifier, also the external identifier (§1.6).</summary>
    public EmployeeId Id { get; private init; }

    /// <summary>
    /// The Company this Employee belongs to.
    /// </summary>
    /// <remarks>
    /// The tenant discriminator (DB-P1, AT-4), and the column every row-level security policy in
    /// the identity schema compares against. <c>init</c>-only because an Employee belongs to
    /// exactly one Company for its whole life — reassignment would move a row across a tenant
    /// boundary, which no operation is permitted to do.
    /// <para>
    /// Carried as an identifier with no foreign key: <c>companies</c> lives in the
    /// <c>tenancy</c> schema and DB-P2 forbids a constraint across module schemas.
    /// </para>
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>Normalized email address, unique per Company among non-deleted rows.</summary>
    public Email Email { get; private set; }

    /// <summary>When the address was verified, or <see langword="null"/> if it has not been.</summary>
    public DateTimeOffset? EmailVerifiedAtUtc { get; private set; }

    /// <summary>Lifecycle state.</summary>
    public EmployeeStatus Status { get; private set; }

    /// <summary>
    /// The Employee's primary Team, if assigned.
    /// </summary>
    /// <remarks>
    /// An untyped identifier for the same reason as <see cref="CompanyId"/> is not a foreign key:
    /// Teams belong to the <c>tenancy</c> module. It stays a <c>Guid</c> until that module
    /// publishes a contract to type it against — introducing a <c>TeamId</c> here would be this
    /// module defining another's vocabulary.
    /// </remarks>
    public Guid? PrimaryTeamId { get; private set; }

    /// <summary>When the Employee was soft-deleted (§1.8).</summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>Who performed the soft delete.</summary>
    public EmployeeId? DeletedByEmployeeId { get; private set; }

    /// <summary>
    /// When identifying data was cleared in response to an erasure request.
    /// </summary>
    /// <remarks>
    /// SD-018 resolves erasure (NFR-PRIV-009) against audit immutability (NFR-DATA-006) by
    /// pseudonymizing rather than deleting: the identity is cleared and the row persists so
    /// ledger attribution survives. <b>Its legal adequacy is unconfirmed</b> — SD-018 is open, and
    /// database-design §4.2 states that if the position changes, this column and the erasure path
    /// change with it.
    /// </remarks>
    public DateTimeOffset? PseudonymizedAtUtc { get; private set; }

    /// <summary>Row creation (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>Actor who created the row; null for system-created rows.</summary>
    public EmployeeId? CreatedByEmployeeId { get; private set; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Actor who last modified the row.</summary>
    public EmployeeId? UpdatedByEmployeeId { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>Whether the Employee has been soft-deleted.</summary>
    public bool IsDeleted => DeletedAtUtc is not null;

    /// <summary>
    /// Creates an invited Employee.
    /// </summary>
    /// <remarks>
    /// The only way an Employee comes into existence, and it starts at
    /// <see cref="EmployeeStatus.Invited"/> — nothing may create an already-active account,
    /// because activation is what accepting an invitation means.
    /// </remarks>
    /// <exception cref="ArgumentException">The Company identifier is unset.</exception>
    public static Employee Invite(
        CompanyId companyId,
        Email email,
        DateTimeOffset invitedAtUtc,
        EmployeeId? invitedBy = null)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (companyId.IsEmpty)
        {
            // An Employee with no Company is a row no tenant policy matches and no caller can
            // ever read back. Rejecting it here keeps that state from reaching the database at
            // all, rather than relying on a NOT NULL that a default Guid would satisfy.
            throw new ArgumentException(
                "An Employee must belong to a Company.", nameof(companyId));
        }

        return new Employee(EmployeeId.New(), companyId, email, invitedAtUtc)
        {
            CreatedByEmployeeId = invitedBy,
            UpdatedByEmployeeId = invitedBy
        };
    }

    /// <summary>
    /// Activates an invited Employee.
    /// </summary>
    /// <remarks>
    /// The transition an accepted invitation performs. Returns a result rather than throwing
    /// because "already active" is an expected outcome — a caller double-submitting a form, or
    /// retrying after a timeout — not an exceptional one (EX-1).
    /// <para>
    /// <b>Activation verifies the email address.</b> FR-AUTH-013 requires verification before an
    /// account becomes active, and completing an invitation is that proof: the token was
    /// delivered to the address and came back. Leaving <c>email_verified_at_utc</c> null here
    /// would mean either an active-but-unverified account, which FR-AUTH-013 forbids, or a
    /// second verification round trip to the address that just demonstrated it works.
    /// </para>
    /// <para>
    /// The rule lives here rather than in the calling handler so there is one definition of when
    /// an Employee may become active, whatever reaches it.
    /// </para>
    /// </remarks>
    public Result Activate(DateTimeOffset activatedAtUtc)
    {
        if (Status == EmployeeStatus.Active)
        {
            return Result.Failure(Error.Conflict("The Employee is already active."));
        }

        if (Status != EmployeeStatus.Invited)
        {
            // Suspended and Removed are not reachable by accepting an invitation. Reinstating
            // either is an administrative act with its own authorization and audit trail.
            return Result.Failure(Error.Conflict(
                $"An Employee with status {Status} cannot be activated by accepting an invitation."));
        }

        if (IsDeleted)
        {
            return Result.Failure(Error.Conflict("A removed Employee cannot be activated."));
        }

        Status = EmployeeStatus.Active;
        EmailVerifiedAtUtc = activatedAtUtc;
        UpdatedAtUtc = activatedAtUtc;

        return Result.Success();
    }
}
