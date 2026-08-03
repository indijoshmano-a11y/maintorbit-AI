using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <see cref="PasswordResetToken"/> to <c>identity.password_reset_tokens</c>.</summary>
internal sealed class PasswordResetTokenConfiguration
    : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PasswordResetTokens",
            Schemas.Identity,
            table => table.HasCheckConstraint(
                "ck_password_reset_tokens_expiry",
                "expires_at_utc > requested_at_utc"));

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.CompanyId).IsRequired();
        builder.Property(token => token.EmployeeId).IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(PasswordResetTokenHash.Length)
            .IsRequired();

        builder.Property(token => token.RequestedAtUtc).IsRequired();

        // 45 characters holds an IPv6 address with an embedded IPv4 suffix, which is the longest
        // textual form. Matches sessions.ip_address.
        builder.Property(token => token.RequestedFromIpAddress).HasMaxLength(45);

        builder.Property(token => token.ExpiresAtUtc).IsRequired();
        builder.Property(token => token.ConsumedAtUtc);
        builder.Property(token => token.InvalidatedAtUtc);

        builder.Property(token => token.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(token => token.IsConsumed);
        builder.Ignore(token => token.IsInvalidated);

        // Unique, and the only way a token is ever found: the plaintext is not stored, so lookup
        // is by digest. Uniqueness also makes a hash collision a write failure rather than a
        // silent match against somebody else's reset.
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_password_reset_tokens_token_hash");

        // Superseding outstanding links sweeps by this, on both the request and the completion
        // path. Partial, because the sweep only ever touches rows that are still live and those
        // are a small minority of what accumulates.
        builder.HasIndex(token => token.EmployeeId)
            .HasFilter("consumed_at_utc IS NULL AND invalidated_at_utc IS NULL")
            .HasDatabaseName("ix_password_reset_tokens_employee_id_outstanding");

        // No foreign key to employees is declared beyond this one because both tables live in the
        // identity schema — DB-P2 forbids crossing schemas, not staying within one. Cascade
        // matches refresh_tokens: a deleted Employee leaves no live recovery credential behind.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(token => token.EmployeeId)
            .HasConstraintName("fk_password_reset_tokens_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
