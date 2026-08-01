using MaintOrbit.Domain.Modules.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <see cref="Session"/> to <c>identity.sessions</c>.</summary>
internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Sessions",
            Schemas.Identity,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_sessions_client_type",
                    "client_type IN ('Unknown', 'WebConsole', 'VsCodeExtension', 'ServerApplication')");

                // A session that expires before it begins is unusable and indistinguishable from
                // one that expired legitimately. The aggregate refuses it; so does the database.
                table.HasCheckConstraint(
                    "ck_sessions_absolute_expiry",
                    "absolute_expires_at_utc > created_at_utc");

                // revoked_at_utc and revocation_reason are set together or not at all — a reason
                // without a revocation, or a revocation with no reason, is a row nothing can
                // interpret.
                table.HasCheckConstraint(
                    "ck_sessions_revocation",
                    "(revoked_at_utc IS NULL) = (revocation_reason IS NULL)");
            });

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.CompanyId).IsRequired();
        builder.Property(session => session.EmployeeId).IsRequired();

        builder.Property(session => session.DeviceLabel).HasMaxLength(128);

        builder.Property(session => session.ClientType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // Wide enough for an IPv6 address with a zone identifier.
        builder.Property(session => session.IpAddress).HasMaxLength(64);
        builder.Property(session => session.CoarseLocation).HasMaxLength(128);

        builder.Property(session => session.CreatedAtUtc).IsRequired();
        builder.Property(session => session.LastActiveAtUtc).IsRequired();
        builder.Property(session => session.AbsoluteExpiresAtUtc).IsRequired();
        builder.Property(session => session.UpdatedAtUtc).IsRequired();

        builder.Property(session => session.RevokedAtUtc);
        builder.Property(session => session.RevocationReason)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(session => session.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(session => session.IsRevoked);

        // Partial, per §4.2 — the index serves "which sessions are live for this Employee", which
        // is the device list (FR-AUTH-008) and the bulk-revocation sweep. Revoked rows are dead
        // weight in it.
        builder.HasIndex(session => session.EmployeeId)
            .HasDatabaseName("ix_sessions_employee_id_active")
            .HasFilter("revoked_at_utc IS NULL");

        builder.HasIndex(session => session.CompanyId)
            .HasDatabaseName("ix_sessions_company_id");

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(session => session.EmployeeId)
            .HasConstraintName("fk_sessions_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
