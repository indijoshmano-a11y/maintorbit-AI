using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>
/// EF Core implementation of <see cref="IEmployeeRepository"/>.
/// </summary>
/// <remarks>
/// Deliberately thin. The tenant predicate is absent because row-level security applies it below
/// the application layer, and change tracking makes an explicit update call unnecessary — a
/// loaded aggregate that is mutated is already part of the unit of work.
/// </remarks>
internal sealed class EmployeeRepository(MaintOrbitDbContext context) : IEmployeeRepository
{
    /// <inheritdoc />
    public Task<Employee?> FindAsync(EmployeeId id, CancellationToken cancellationToken) =>
        // Tracked, not AsNoTracking: the caller activates the Employee, and an untracked
        // aggregate would be mutated in memory and silently never written.
        context.Employees.FirstOrDefaultAsync(employee => employee.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Employee employee) => context.Employees.Add(employee);
}
