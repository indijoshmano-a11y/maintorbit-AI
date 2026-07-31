using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <see cref="EmployeeCredential"/> to <c>identity.employee_credentials</c>.
/// </summary>
internal sealed class EmployeeCredentialConfiguration : IEntityTypeConfiguration<EmployeeCredential>
{
    public void Configure(EntityTypeBuilder<EmployeeCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "EmployeeCredentials",
            Schemas.Identity,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_employee_credentials_algorithm",
                    "algorithm IN ('Argon2id')");

                // A negative failure count is not a state any code path should be able to write,
                // and a version below one would mean a hash produced by no recorded parameter
                // set. Both are cheap to assert and impossible to notice otherwise.
                table.HasCheckConstraint(
                    "ck_employee_credentials_failed_login_count",
                    "failed_login_count >= 0");

                table.HasCheckConstraint(
                    "ck_employee_credentials_password_version",
                    "password_version >= 1");
            });

        builder.HasKey(credential => credential.Id);

        builder.Property(credential => credential.Id).ValueGeneratedNever();

        builder.Property(credential => credential.CompanyId).IsRequired();
        builder.Property(credential => credential.EmployeeId).IsRequired();

        builder.Property(credential => credential.PasswordHash)
            .HasMaxLength(PasswordHash.MaxLength)
            .IsRequired();

        builder.Property(credential => credential.Algorithm)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(credential => credential.PasswordVersion).IsRequired();

        builder.Property(credential => credential.HashParameters)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(credential => credential.PasswordChangedAtUtc).IsRequired();
        builder.Property(credential => credential.RequirePasswordChange).IsRequired();
        builder.Property(credential => credential.FailedLoginCount).IsRequired();
        builder.Property(credential => credential.LockoutUntilUtc);

        builder.Property(credential => credential.CreatedAtUtc).IsRequired();
        builder.Property(credential => credential.CreatedByEmployeeId);
        builder.Property(credential => credential.UpdatedAtUtc).IsRequired();
        builder.Property(credential => credential.UpdatedByEmployeeId);

        builder.Property(credential => credential.RowVersion)
            .IsConcurrencyToken()
            .IsRequired();

        // One credential per Employee at most (1 : 0..1). Unique on employee_id alone rather
        // than on (company_id, employee_id): an EmployeeId is globally unique, so scoping the
        // constraint to a Company would permit a second credential for the same Employee under a
        // different company_id — a row that row-level security would then hide from the Company
        // that owns the Employee.
        builder.HasIndex(credential => credential.EmployeeId)
            .IsUnique();

        // Foreign key within the identity schema, which DB-P2 permits and §3.3 shows for the
        // sibling relationship sessions.employee_id -> employees.id. Cascade because a credential
        // has no meaning without its Employee.
        builder.HasOne<Employee>()
            .WithOne()
            .HasForeignKey<EmployeeCredential>(credential => credential.EmployeeId)
            .HasConstraintName("fk_employee_credentials_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
