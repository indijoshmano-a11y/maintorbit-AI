using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.Modules.Identity.Repositories;

/// <summary>
/// Access to <see cref="Employee"/> aggregates.
/// </summary>
/// <remarks>
/// Owned by the identity module and resolved only inside it (AT-5). One method per question the
/// module actually asks — not a generic repository, which would expose <c>IQueryable</c> and make
/// every caller able to compose any query, including ones that defeat the loading rules ADR-0023
/// relies on.
/// <para>
/// <b>Nothing here filters by Company.</b> That is not an omission: row-level security applies the
/// tenant predicate below the application layer (ADR-0005, §5.1), so a repository that also
/// filtered would be a second, discretionary copy of the control — and the one that gets forgotten
/// is the one that matters. NFR-SEC-007 requires an application-layer defect to be unable to cause
/// cross-tenant exposure, which is only true if the application is not where the filtering lives.
/// </para>
/// </remarks>
public interface IEmployeeRepository
{
    /// <summary>
    /// Finds an Employee by identifier, or <see langword="null"/> if none is visible.
    /// </summary>
    /// <remarks>
    /// "Visible" is doing work: an Employee belonging to another Company is invisible under the
    /// active tenant context and is indistinguishable from one that does not exist. §6.2 defines
    /// <c>not_found</c> as covering both for exactly that reason.
    /// </remarks>
    Task<Employee?> FindAsync(EmployeeId id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an Employee by email address, or <see langword="null"/> if none is visible.
    /// </summary>
    /// <remarks>
    /// Resolves against <c>ux_employees_company_id_email</c>, which is unique <b>per Company</b>
    /// and excludes soft-deleted rows — so within a tenant context this returns at most one, and a
    /// removed Employee's address does not resurrect their account.
    /// <para>
    /// <b>Tenant-scoped, like every other read.</b> Row-level security applies, so this finds
    /// nothing without a Company in scope. That places a real constraint on the caller, recorded
    /// on the handler: how a login request determines which Company it is for is not documented,
    /// and this milestone does not invent an answer.
    /// </para>
    /// </remarks>
    Task<Employee?> FindByEmailAsync(Email email, CancellationToken cancellationToken);

    /// <summary>
    /// A page of the Company's Employees, oldest first.
    /// </summary>
    /// <remarks>
    /// <b>No Company parameter.</b> Row-level security applies the tenant predicate below the
    /// application layer, so this returns the Company in scope and nothing else — and a
    /// discretionary filter here would be a second copy of the control, which is the one that
    /// gets forgotten (NFR-SEC-007).
    /// <para>
    /// Ordered by identifier, which is UUIDv7 and therefore time-ordered (§1.6). §5.2 requires a
    /// deterministic order with ties broken by <c>id</c>; ordering by <c>id</c> alone gives both
    /// at once, and it is the primary key, so the page is served from an index rather than a sort.
    /// </para>
    /// <para>
    /// Soft-deleted Employees are excluded. §4.2 retains removed rows so ledger and audit records
    /// stay attributed (FR-TEN-008); a directory that listed them would show every person who has
    /// ever left.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Employee>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// How many Employees the Company has.
    /// </summary>
    /// <remarks>
    /// §4.4 carries a total on small bounded collections because "counting them is cheap and the
    /// UI benefits". The same filter as <see cref="ListAsync"/>, so the total describes the
    /// collection being paged rather than a larger one.
    /// </remarks>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new Employee to the unit of work.
    /// </summary>
    /// <remarks>
    /// Synchronous because it only tracks the aggregate; nothing reaches the database until the
    /// unit of work commits.
    /// </remarks>
    void Add(Employee employee);
}
