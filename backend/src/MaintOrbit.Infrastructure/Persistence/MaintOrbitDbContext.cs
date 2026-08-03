using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
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
    /// <summary>
    /// Employees — the identity module's aggregate root.
    /// </summary>
    /// <remarks>
    /// Exposed as a set because the identity module owns it. Sets are added per aggregate root,
    /// not per table: <c>employee_credentials</c>, <c>sessions</c>, and the rest are reached
    /// through their own aggregates, and <c>employee_credentials</c> in particular is C4 data
    /// that must never be loaded alongside an ordinary Employee read.
    /// </remarks>
    public DbSet<Employee> Employees => Set<Employee>();

    /// <summary>
    /// Password credentials — C4 data.
    /// </summary>
    /// <remarks>
    /// A separate set from <see cref="Employees"/> deliberately. §4.2 classifies this table C4:
    /// never logged, never in error messages, never leaves production. Reaching it must be an
    /// explicit act, so that an ordinary Employee read cannot pull a password hash into memory
    /// as a side effect of loading a navigation property.
    /// </remarks>
    public DbSet<EmployeeCredential> EmployeeCredentials => Set<EmployeeCredential>();

    /// <summary>Device-scoped authenticated sessions (SD-016).</summary>
    public DbSet<Session> Sessions => Set<Session>();

    /// <summary>Refresh tokens — C4, stored only as hashes (SD-014).</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>The atomic permission catalogue — platform-wide reference data (§4.2).</summary>
    public DbSet<Permission> Permissions => Set<Permission>();

    /// <summary>Role definitions — seven fixed now, customer-composed at v2.0 (FR-PERM-006).</summary>
    public DbSet<RoleDefinition> RoleDefinitions => Set<RoleDefinition>();

    /// <summary>Which permissions each role grants.</summary>
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    /// <summary>Which roles an Employee holds, and at what scope — tenant-scoped.</summary>
    public DbSet<EmployeeRole> EmployeeRoles => Set<EmployeeRole>();

    /// <summary>
    /// Registers value-object conversions before the model is discovered.
    /// </summary>
    /// <remarks>
    /// Must happen here rather than per property. EF discovers entity types before entity
    /// configurations run, so a value object that is a reference type is discovered as an entity
    /// and a later HasConversion leaves that stray type in the model.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<EmployeeId>()
            .HaveConversion<ValueObjectConverters.EmployeeIdConverter>();

        configurationBuilder.Properties<EmployeeCredentialId>()
            .HaveConversion<ValueObjectConverters.EmployeeCredentialIdConverter>();

        configurationBuilder.Properties<PasswordHash>()
            .HaveConversion<ValueObjectConverters.PasswordHashConverter>()
            .HaveMaxLength(PasswordHash.MaxLength);

        configurationBuilder.Properties<SessionId>()
            .HaveConversion<ValueObjectConverters.SessionIdConverter>();

        configurationBuilder.Properties<RefreshTokenId>()
            .HaveConversion<ValueObjectConverters.RefreshTokenIdConverter>();

        configurationBuilder.Properties<RefreshTokenFamilyId>()
            .HaveConversion<ValueObjectConverters.RefreshTokenFamilyIdConverter>();

        configurationBuilder.Properties<RefreshTokenHash>()
            .HaveConversion<ValueObjectConverters.RefreshTokenHashConverter>()
            .HaveMaxLength(RefreshTokenHash.Length);

        configurationBuilder.Properties<PermissionCode>()
            .HaveConversion<ValueObjectConverters.PermissionCodeConverter>()
            .HaveMaxLength(PermissionCode.MaxLength);

        configurationBuilder.Properties<RoleCode>()
            .HaveConversion<ValueObjectConverters.RoleCodeConverter>()
            .HaveMaxLength(RoleCode.MaxLength);

        configurationBuilder.Properties<CompanyId>()
            .HaveConversion<ValueObjectConverters.CompanyIdConverter>();

        configurationBuilder.Properties<Email>()
            .HaveConversion<ValueObjectConverters.EmailConverter>()
            .HaveMaxLength(Email.MaxLength);
    }

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
