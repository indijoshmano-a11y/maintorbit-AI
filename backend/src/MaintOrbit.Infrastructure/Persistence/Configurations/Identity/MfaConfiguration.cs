using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <see cref="MfaEnrollment"/> to <c>identity.mfa_enrollments</c>.</summary>
internal sealed class MfaEnrollmentConfiguration : IEntityTypeConfiguration<MfaEnrollment>
{
    public void Configure(EntityTypeBuilder<MfaEnrollment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "MfaEnrollments",
            Schemas.Identity,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_mfa_enrollments_status",
                    "status IN ('Pending', 'Confirmed', 'Disabled')");

                // A confirmed enrolment has a confirmation instant and a pending one does not.
                // Without this the two could disagree, and "is this factor live?" would have two
                // answers on the same row.
                table.HasCheckConstraint(
                    "ck_mfa_enrollments_confirmation",
                    "(status = 'Pending') = (confirmed_at_utc IS NULL)");
            });

        builder.HasKey(enrollment => enrollment.Id);
        builder.Property(enrollment => enrollment.Id).ValueGeneratedNever();

        builder.Property(enrollment => enrollment.CompanyId).IsRequired();
        builder.Property(enrollment => enrollment.EmployeeId).IsRequired();

        builder.Property(enrollment => enrollment.Method)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(enrollment => enrollment.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Owned rather than converted to a single column. The envelope is five values that must
        // be written and read together, and §4.3 keeps them as separate columns on
        // provider_connections for the same reason: a blob would make "which DEK version wrote
        // this?" unanswerable without decrypting it.
        builder.OwnsOne(enrollment => enrollment.Secret, secret =>
        {
            // The owned type shares the table, so its key must be the owner's key column. EF names
            // the shadow property MfaEnrollmentId, which the snake_case convention would then turn
            // into a second column called mfa_enrollment_id — and EF rejects a primary key mapped
            // to two different columns. Naming it here settles that before the convention runs.
            secret.WithOwner().HasForeignKey("MfaEnrollmentId");
            secret.Property("MfaEnrollmentId").HasColumnName("id");

            secret.Property(envelope => envelope.Ciphertext)
                .HasColumnName("secret_ciphertext")
                .IsRequired();

            secret.Property(envelope => envelope.Nonce)
                .HasColumnName("secret_iv")
                .HasMaxLength(SecretEnvelope.NonceLength)
                .IsRequired();

            secret.Property(envelope => envelope.AuthenticationTag)
                .HasColumnName("secret_auth_tag")
                .HasMaxLength(SecretEnvelope.TagLength)
                .IsRequired();

            secret.Property(envelope => envelope.DekVersion)
                .HasColumnName("dek_version")
                .IsRequired();

            secret.Property(envelope => envelope.AlgorithmId)
                .HasColumnName("algorithm_id")
                .HasMaxLength(32)
                .IsRequired();
        });

        builder.Navigation(enrollment => enrollment.Secret).IsRequired();

        builder.Property(enrollment => enrollment.LastAcceptedTimeStep);
        builder.Property(enrollment => enrollment.CreatedAtUtc).IsRequired();
        builder.Property(enrollment => enrollment.ConfirmedAtUtc);
        builder.Property(enrollment => enrollment.LastVerifiedAtUtc);
        builder.Property(enrollment => enrollment.DisabledAtUtc);
        builder.Property(enrollment => enrollment.UpdatedAtUtc).IsRequired();

        builder.Property(enrollment => enrollment.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(enrollment => enrollment.IsActive);
        builder.Ignore(enrollment => enrollment.IsPending);

        // Partial and unique: at most one live enrolment per Employee, with disabled rows kept as
        // history. Enforced by the database rather than only by the handler, because the handler's
        // check and a concurrent request are not the same guarantee.
        builder.HasIndex(enrollment => enrollment.EmployeeId)
            .IsUnique()
            .HasFilter("disabled_at_utc IS NULL")
            .HasDatabaseName("ux_mfa_enrollments_employee_id_active");

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(enrollment => enrollment.EmployeeId)
            .HasConstraintName("fk_mfa_enrollments_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps <see cref="MfaRecoveryCode"/> to <c>identity.mfa_recovery_codes</c>.</summary>
internal sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MfaRecoveryCodes", Schemas.Identity);

        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();

        builder.Property(code => code.CompanyId).IsRequired();
        builder.Property(code => code.EmployeeId).IsRequired();
        builder.Property(code => code.EnrollmentId).IsRequired();

        builder.Property(code => code.CodeHash)
            .HasMaxLength(RecoveryCodeHash.Length)
            .IsRequired();

        builder.Property(code => code.IssuedAtUtc).IsRequired();
        builder.Property(code => code.UsedAtUtc);

        builder.Property(code => code.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(code => code.IsUsed);

        // Unique per enrolment, not globally: two Employees could in principle be issued the same
        // code, and a global constraint would make that a write failure for the second one rather
        // than the coincidence it is. Lookup is always within an enrolment, so this is also the
        // index that serves it.
        builder.HasIndex(code => new { code.EnrollmentId, code.CodeHash })
            .IsUnique()
            .HasDatabaseName("ux_mfa_recovery_codes_enrollment_id_code_hash");

        builder.HasOne<MfaEnrollment>()
            .WithMany()
            .HasForeignKey(code => code.EnrollmentId)
            .HasConstraintName("fk_mfa_recovery_codes_mfa_enrollments_enrollment_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
