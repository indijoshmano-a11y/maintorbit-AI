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
    /// Adds a new Employee to the unit of work.
    /// </summary>
    /// <remarks>
    /// Synchronous because it only tracks the aggregate; nothing reaches the database until the
    /// unit of work commits.
    /// </remarks>
    void Add(Employee employee);
}
