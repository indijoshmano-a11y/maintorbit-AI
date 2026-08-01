using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.Modules.Identity.Entities;

/// <summary>
/// An Employee's password credential — C4 data.
/// </summary>
/// <remarks>
/// A separate aggregate from <see cref="Employee"/>, not a property of it, and the separation is
/// the security control rather than a modelling preference. <c>employee_credentials</c> is C4:
/// never logged, never in error messages, never leaves production. An ordinary Employee read —
/// listing the directory, resolving an invitation, rendering a profile — must not carry a
/// password hash into memory at all, and the only reliable way to guarantee that is for the two
/// to be separately loadable.
/// <para>
/// The relationship is <b>1 : 0..1</b> (§3.x). A credential is absent for a federated-only
/// Employee, and FR-AUTH-004 lets a Company disable password authentication entirely — so an
/// Employee without one is an ordinary state, not an incomplete record.
/// </para>
/// <para>
/// <b>State only.</b> Lockout counters and timestamps are stored here; nothing increments,
/// evaluates, or expires them. Those are authentication workflow, and this milestone is the
/// model.
/// </para>
/// </remarks>
public sealed class EmployeeCredential
{
    /// <summary>
    /// Constructor for the persistence layer.
    /// </summary>
    /// <remarks>
    /// Private so a credential can only come from <see cref="Establish"/>. EF materializes
    /// through it, which correctly bypasses the invariants — a stored row satisfied them when it
    /// was written.
    /// </remarks>
    private EmployeeCredential()
    {
        PasswordHash = null!;
        HashParameters = null!;
    }

    private EmployeeCredential(
        EmployeeCredentialId id,
        CompanyId companyId,
        EmployeeId employeeId,
        PasswordHash passwordHash,
        PasswordAlgorithm algorithm,
        int passwordVersion,
        string hashParameters,
        DateTimeOffset establishedAtUtc)
    {
        Id = id;
        CompanyId = companyId;
        EmployeeId = employeeId;
        PasswordHash = passwordHash;
        Algorithm = algorithm;
        PasswordVersion = passwordVersion;
        HashParameters = hashParameters;
        PasswordChangedAtUtc = establishedAtUtc;
        CreatedAtUtc = establishedAtUtc;
        UpdatedAtUtc = establishedAtUtc;
    }

    /// <summary>Identifier.</summary>
    public EmployeeCredentialId Id { get; private init; }

    /// <summary>
    /// The Company this credential belongs to.
    /// </summary>
    /// <remarks>
    /// Carried directly rather than reached through the Employee. DB-P1 requires the tenant
    /// discriminator on <i>every</i> tenant-scoped relation, and the row-level security policy
    /// compares against a column on this table — a policy that had to join to
    /// <c>employees</c> would be evaluated per row on the most sensitive table in the schema.
    /// </remarks>
    public CompanyId CompanyId { get; private init; }

    /// <summary>The Employee this credential authenticates.</summary>
    public EmployeeId EmployeeId { get; private init; }

    /// <summary>The stored hash. Never logged, never returned.</summary>
    public PasswordHash PasswordHash { get; private set; }

    /// <summary>Which key derivation function produced <see cref="PasswordHash"/>.</summary>
    public PasswordAlgorithm Algorithm { get; private set; }

    /// <summary>
    /// Which generation of cost parameters produced the hash.
    /// </summary>
    /// <remarks>
    /// An integer rather than a parse of <see cref="HashParameters"/>, so "every credential still
    /// on last year's parameters" is an indexed query. SD-010 reviews parameters annually, and a
    /// review is only actionable if the rows needing re-hashing can be found.
    /// </remarks>
    public int PasswordVersion { get; private set; }

    /// <summary>
    /// The exact cost parameters used, recorded per row.
    /// </summary>
    /// <remarks>
    /// §4.2 states the reason: stored per row "so that a parameter change (annual review, SD-010)
    /// does not invalidate existing hashes". A hash produced under old parameters stays
    /// verifiable because the row says what they were, so a review does not lock anybody out.
    /// </remarks>
    public string HashParameters { get; private set; }

    /// <summary>When the password was last set (§4.2).</summary>
    public DateTimeOffset PasswordChangedAtUtc { get; private set; }

    /// <summary>
    /// Whether the Employee must set a new password before proceeding.
    /// </summary>
    /// <remarks>
    /// Supports the administrative reset path around FR-AUTH-012. It is <b>not</b> password
    /// ageing: compliance §14 lists no expiry, and prefers breach-corpus checking to rotation —
    /// a password that is long, unique, and unbreached is stronger than one rotated on a
    /// schedule.
    /// </remarks>
    public bool RequirePasswordChange { get; private set; }

    /// <summary>Consecutive failed authentication attempts (FR-AUTH-011).</summary>
    public int FailedLoginCount { get; private set; }

    /// <summary>
    /// When a lockout ends, or <see langword="null"/> if the account is not locked.
    /// </summary>
    /// <remarks>
    /// FR-AUTH-011 locks after a configurable number of failures. Held in the database rather
    /// than only in the rate limiter's cache because a lockout that evaporates when Redis
    /// restarts is not a lockout — and 07-api-security T-3 notes lockout is itself a
    /// denial-of-service vector, which makes a durable, inspectable end time necessary rather
    /// than optional.
    /// </remarks>
    public DateTimeOffset? LockoutUntilUtc { get; private set; }

    /// <summary>Row creation (§1.7).</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>Actor who created the row; null when the Employee set it themselves.</summary>
    public EmployeeId? CreatedByEmployeeId { get; private set; }

    /// <summary>Last modification (§1.7).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Actor who last modified the row.</summary>
    public EmployeeId? UpdatedByEmployeeId { get; private set; }

    /// <summary>Optimistic concurrency token (§1.7).</summary>
    public int RowVersion { get; private set; }

    /// <summary>
    /// Whether a lockout is currently in force.
    /// </summary>
    /// <remarks>
    /// Reads the state FR-AUTH-011 defines; it does not maintain it. Nothing in this build sets
    /// <see cref="LockoutUntilUtc"/> yet — counting failures and notifying the holder is
    /// authentication workflow and lands with it. The check exists now because the alternative
    /// is a column that is enforced only once somebody remembers to add the check, and by then
    /// the lockout has been silently ineffective for however long.
    /// <para>
    /// <b>No verification method lives on this aggregate.</b> Verifying a password requires the
    /// key derivation function, and the domain cannot reach it: <c>IPasswordHasher</c> is an
    /// application port, and a domain type depending on it would invert the dependency rule
    /// ADR-0001 fixes — which an architecture test enforces by asserting the Domain project
    /// carries no package reference at all. The aggregate owns the rules about its own state;
    /// the cryptography stays outside it.
    /// </para>
    /// </remarks>
    public bool IsLockedOut(DateTimeOffset asAtUtc) =>
        LockoutUntilUtc is { } until && until > asAtUtc;

    /// <summary>
    /// Establishes a password credential for an Employee.
    /// </summary>
    /// <remarks>
    /// Takes an already-computed hash. Nothing here hashes, and nothing here accepts a plaintext
    /// password — a domain type that could hold one is a domain type that can log one.
    /// </remarks>
    /// <exception cref="ArgumentException">The Company or Employee identifier is unset, the
    /// parameters are blank, or the version is not positive.</exception>
    public static EmployeeCredential Establish(
        CompanyId companyId,
        EmployeeId employeeId,
        PasswordHash passwordHash,
        PasswordAlgorithm algorithm,
        int passwordVersion,
        string hashParameters,
        DateTimeOffset establishedAtUtc,
        EmployeeId? establishedBy = null)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashParameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(passwordVersion, 1);

        if (companyId.IsEmpty)
        {
            throw new ArgumentException(
                "A credential must belong to a Company.", nameof(companyId));
        }

        if (employeeId.IsEmpty)
        {
            // A credential with no Employee authenticates nobody and would be unreachable, but
            // it would still be a hash sitting in the most sensitive table in the schema.
            throw new ArgumentException(
                "A credential must belong to an Employee.", nameof(employeeId));
        }

        return new EmployeeCredential(
            EmployeeCredentialId.New(),
            companyId,
            employeeId,
            passwordHash,
            algorithm,
            passwordVersion,
            hashParameters,
            establishedAtUtc)
        {
            CreatedByEmployeeId = establishedBy,
            UpdatedByEmployeeId = establishedBy
        };
    }
}
