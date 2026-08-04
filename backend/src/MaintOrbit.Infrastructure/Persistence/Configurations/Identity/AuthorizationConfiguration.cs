using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps the permission catalogue.</summary>
/// <remarks>
/// Platform-wide reference data: the set of things that <i>can</i> be permitted is identical for
/// every Company. It carries no <c>company_id</c> and no policy — the one deliberate exception to
/// DB-P1 in this schema, and a safe one, because a catalogue row names a capability and grants
/// nothing to anybody.
/// </remarks>
internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Permissions", Schemas.Identity);

        // The code is the key (§1.6 reference data), so a role definition reads as a list of
        // permission names rather than a list of opaque identifiers.
        builder.HasKey(permission => permission.Code);
        builder.Property(permission => permission.Code)
            .HasMaxLength(PermissionCode.MaxLength)
            .ValueGeneratedNever();

        builder.Property(permission => permission.Description).HasMaxLength(256).IsRequired();
    }
}

/// <summary>Maps role definitions.</summary>
internal sealed class RoleDefinitionConfiguration : IEntityTypeConfiguration<RoleDefinition>
{
    public void Configure(EntityTypeBuilder<RoleDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RoleDefinitions", Schemas.Identity);

        builder.HasKey(role => role.Code);
        builder.Property(role => role.Code)
            .HasMaxLength(RoleCode.MaxLength)
            .ValueGeneratedNever();

        builder.Property(role => role.Name).HasMaxLength(64).IsRequired();
        builder.Property(role => role.IsBuiltIn).IsRequired();
    }
}

/// <summary>Maps the role-to-permission grants.</summary>
internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RolePermissions", Schemas.Identity);

        // The pair is the key: a role either grants a permission or does not, so there is no second
        // grant to distinguish and no surrogate worth carrying.
        builder.HasKey(grant => new { grant.RoleCode, grant.PermissionCode });

        builder.Property(grant => grant.RoleCode).HasMaxLength(RoleCode.MaxLength);
        builder.Property(grant => grant.PermissionCode).HasMaxLength(PermissionCode.MaxLength);

        builder.HasOne<RoleDefinition>()
            .WithMany()
            .HasForeignKey(grant => grant.RoleCode)
            .HasConstraintName("fk_role_permissions_role_definitions_role_code")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(grant => grant.PermissionCode)
            .HasConstraintName("fk_role_permissions_permissions_permission_code")
            // Restrict, not cascade: removing a permission from the catalogue while roles still
            // grant it should fail loudly rather than silently narrow what those roles allow.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(grant => grant.RoleCode)
            .HasDatabaseName("ix_role_permissions_role_code");
    }
}

/// <summary>Maps role assignments — the tenant-scoped half of authorization.</summary>
internal sealed class EmployeeRoleConfiguration : IEntityTypeConfiguration<EmployeeRole>
{
    public void Configure(EntityTypeBuilder<EmployeeRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "EmployeeRoles",
            Schemas.Identity,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_employee_roles_scope_type",
                    "scope_type IN ('Company', 'Team', 'Self')");

                // A Team-scoped assignment with no Team reaches nothing; any other scope carrying
                // one implies a limit that is not enforced. The aggregate refuses both; so does
                // the database, for anything writing around it.
                table.HasCheckConstraint(
                    "ck_employee_roles_scope_target",
                    "(scope_type = 'Team') = (scope_id IS NOT NULL)");
            });

        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).ValueGeneratedNever();

        builder.Property(role => role.CompanyId).IsRequired();
        builder.Property(role => role.EmployeeId).IsRequired();

        builder.Property(role => role.RoleCode).HasMaxLength(RoleCode.MaxLength).IsRequired();

        builder.Property(role => role.ScopeType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(role => role.ScopeId);

        builder.Property(role => role.CreatedAtUtc).IsRequired();
        builder.Property(role => role.CreatedByEmployeeId);
        builder.Property(role => role.UpdatedAtUtc).IsRequired();
        builder.Property(role => role.RowVersion).IsConcurrencyToken().IsRequired();

        // The resolution query's only filter. Every authenticated request that checks a permission
        // runs it, so it is the one index on this table that must exist.
        builder.HasIndex(role => role.EmployeeId)
            .HasDatabaseName("ix_employee_roles_employee_id");

        builder.HasIndex(role => role.CompanyId)
            .HasDatabaseName("ix_employee_roles_company_id");

        // One assignment of a role at a scope. A duplicate grants nothing extra and would appear
        // twice in every resolution.
        //
        // AreNullsDistinct(false) is what makes that true for the common case. PostgreSQL treats
        // NULLs as distinct in a unique index by default, and scope_id is NULL for every
        // Company- and Self-scoped assignment — so without this the constraint prevents duplicates
        // only for Team scope, which is the rarest of the three. It looked correct and enforced
        // almost nothing.
        builder.HasIndex(role => new { role.EmployeeId, role.RoleCode, role.ScopeType, role.ScopeId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_employee_roles_employee_id_role_code_scope");

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(role => role.EmployeeId)
            .HasConstraintName("fk_employee_roles_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RoleDefinition>()
            .WithMany()
            .HasForeignKey(role => role.RoleCode)
            .HasConstraintName("fk_employee_roles_role_definitions_role_code")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
