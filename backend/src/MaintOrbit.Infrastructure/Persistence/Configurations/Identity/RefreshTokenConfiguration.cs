using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <see cref="RefreshToken"/> to <c>identity.refresh_tokens</c>.</summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "RefreshTokens",
            Schemas.Identity,
            table => table.HasCheckConstraint(
                "ck_refresh_tokens_expiry",
                "expires_at_utc > issued_at_utc"));

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.CompanyId).IsRequired();
        builder.Property(token => token.SessionId).IsRequired();
        builder.Property(token => token.FamilyId).IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(RefreshTokenHash.Length)
            .IsRequired();

        builder.Property(token => token.IssuedAtUtc).IsRequired();
        builder.Property(token => token.ExpiresAtUtc).IsRequired();
        builder.Property(token => token.UsedAtUtc);
        builder.Property(token => token.SupersededById);
        builder.Property(token => token.RevokedAtUtc);

        builder.Property(token => token.RowVersion).IsConcurrencyToken().IsRequired();

        builder.Ignore(token => token.IsUsed);
        builder.Ignore(token => token.IsRevoked);

        // Unique, and the only way a token is ever found: the plaintext is not stored, so lookup is
        // by digest. Uniqueness also makes a hash collision a write failure rather than a silent
        // cross-session match.
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_token_hash");

        // Family revocation sweeps by this. Without it, detecting reuse would trigger a table scan
        // at exactly the moment the system is under attack.
        builder.HasIndex(token => token.FamilyId)
            .HasDatabaseName("ix_refresh_tokens_family_id");

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(token => token.SessionId)
            .HasConstraintName("fk_refresh_tokens_sessions_session_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
