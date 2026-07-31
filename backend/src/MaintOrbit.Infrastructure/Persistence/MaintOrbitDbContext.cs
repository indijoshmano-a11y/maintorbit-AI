using MaintOrbit.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// The application's root <see cref="DbContext"/>.
/// </summary>
/// <remarks>
/// One context across all twelve module schemas, which is what ADR-0002's modular monolith
/// calls for: a shared database with a schema per module, not a database per module. Module
/// boundaries are held by schema ownership and by the absence of cross-schema foreign keys
/// (DB-P2), not by splitting the context.
/// <para>
/// <b>It exposes no <c>DbSet</c>, and that is the correct state today.</b> D-1 — ratifying
/// row-level-security tenancy after prototyping its query cost — is recorded in CLAUDE.md §5 as
/// blocking <i>all schema design</i>. Entities added before that decision would need
/// reworking, and their migrations would already be in the history. Configurations are
/// discovered from this assembly, so entities appear here by being written, not by being
/// registered.
/// </para>
/// </remarks>
public sealed class MaintOrbitDbContext(DbContextOptions<MaintOrbitDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        // Every IEntityTypeConfiguration in this assembly. A new entity is mapped by adding its
        // configuration next to it, with nothing to remember to update here.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MaintOrbitDbContext).Assembly);

        // Order matters. The schema check reads names as the configurations left them, before
        // the naming pass rewrites them, so its error message quotes what the developer wrote.
        modelBuilder.RequireExplicitSchema();
        modelBuilder.ApplySnakeCaseNames();
    }
}
