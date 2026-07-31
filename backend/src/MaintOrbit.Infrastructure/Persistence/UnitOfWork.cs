using MaintOrbit.Application.Abstractions.Persistence;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
/// </summary>
/// <remarks>
/// <c>SaveChangesAsync</c> is already atomic — EF opens a transaction around it when more than one
/// statement is required — so a single commit covering both aggregates needs nothing more than
/// this. An explicit <c>BeginTransaction</c> would be required only to span several calls, which
/// §3.6's "one command, one commit" is written to avoid.
/// <para>
/// Scoped, sharing the same <see cref="MaintOrbitDbContext"/> instance as the repositories
/// resolved alongside it. That shared instance is the unit of work; a second context would commit
/// an empty change set while the tracked aggregates stayed unwritten.
/// </para>
/// </remarks>
internal sealed class UnitOfWork(MaintOrbitDbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
