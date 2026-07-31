namespace MaintOrbit.Application.Abstractions.Persistence;

/// <summary>
/// The transaction boundary of a single command.
/// </summary>
/// <remarks>
/// backend-architecture-overview §3.6 fixes this: "The transaction boundary is the command. One
/// command, one transaction, one commit."
/// <para>
/// A handler that needs to affect another module does <b>not</b> extend its transaction to reach
/// it. It commits its own work and publishes an integration event, and the consuming module
/// reconciles — eventual consistency between modules, deliberately, because it is what keeps
/// AD-014's extraction path open. A distributed transaction would close it.
/// </para>
/// <para>
/// Exposed as a port so the application layer can commit without knowing that EF Core, change
/// tracking, or a database exist (ADR-0001).
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits everything tracked in this unit of work.
    /// </summary>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
