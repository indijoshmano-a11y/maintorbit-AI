using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <see cref="EmailVerificationToken"/> to <c>identity.email_verification_tokens</c>.
/// </summary>
internal sealed class EmailVerificationTokenConfiguration
    : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "EmailVerificationTokens",
            Schemas.Identity,
            table => table.HasCheckConstraint(
                "ck_email_verification_tokens_expiry",
                "expires_at_utc > issued_at_utc"));

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.CompanyId).IsRequired();
        builder.Property(token => token.EmployeeId).IsRequired();

        // The address the token was issued for, kept alongside the Employee rather than reached
        // through them — the point of the column is that the two can differ.
        builder.Property(token => token.Email)
            .HasMaxLength(Email.MaxLength)
            .IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(EmailVerificationTokenHash.Length)
            .IsRequired();

        builder.Property(token => token.IssuedAtUtc).IsRequired();
        builder.Property(token => token.ExpiresAtUtc).IsRequired();
        builder.Property(token => token.ConsumedAtUtc);
        builder.Property(token => token.InvalidatedAtUtc);

        builder.Property(token => token.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(token => token.IsConsumed);
        builder.Ignore(token => token.IsInvalidated);

        // Unique, and the only way a token is ever found: the plaintext is not stored, so lookup
        // is by digest. Uniqueness also makes a hash collision a write failure rather than a
        // silent match against somebody else's verification.
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_email_verification_tokens_token_hash");

        // Superseding outstanding links sweeps by this. Partial, because the sweep only ever
        // touches rows that are still live and those are a small minority of what accumulates.
        builder.HasIndex(token => token.EmployeeId)
            .HasFilter("consumed_at_utc IS NULL AND invalidated_at_utc IS NULL")
            .HasDatabaseName("ix_email_verification_tokens_employee_id_outstanding");

        // Within one schema, so DB-P2 does not apply. Cascade matches the other token tables: a
        // deleted Employee leaves no live credential behind.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(token => token.EmployeeId)
            .HasConstraintName("fk_email_verification_tokens_employees_employee_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
