using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>
/// EF Core implementation of <see cref="IEmployeeCredentialRepository"/>.
/// </summary>
internal sealed class EmployeeCredentialRepository(MaintOrbitDbContext context)
    : IEmployeeCredentialRepository
{
    /// <inheritdoc />
    public Task<bool> ExistsForAsync(EmployeeId employeeId, CancellationToken cancellationToken) =>
        // AnyAsync, so the hash never leaves the database. Loading the aggregate to check for
        // its existence would pull C4 material into memory to answer a yes-or-no question.
        context.EmployeeCredentials
            .AnyAsync(credential => credential.EmployeeId == employeeId, cancellationToken);

    /// <inheritdoc />
    public void Add(EmployeeCredential credential) => context.EmployeeCredentials.Add(credential);
}
