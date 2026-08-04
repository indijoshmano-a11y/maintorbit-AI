using MaintOrbit.Domain.Modules.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaintOrbit.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <see cref="CompanyAuthenticationPolicy"/> to
/// <c>identity.company_authentication_policies</c>.
/// </summary>
internal sealed class CompanyAuthenticationPolicyConfiguration
    : IEntityTypeConfiguration<CompanyAuthenticationPolicy>
{
    public void Configure(EntityTypeBuilder<CompanyAuthenticationPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "CompanyAuthenticationPolicies",
            Schemas.Identity,
            table =>
            {
                // The domain enforces every one of these, and so does the database. A policy is
                // read on the authentication path by code that trusts it, and a row written by a
                // migration, a script, or a future admin tool must not be able to widen a control
                // the aggregate refuses to widen.
                table.HasCheckConstraint(
                    "ck_company_authentication_policies_password_length",
                    $"minimum_password_length BETWEEN {CompanyAuthenticationPolicy.MinimumAllowedPasswordLength} " +
                    $"AND {CompanyAuthenticationPolicy.MaximumAllowedPasswordLength}");

                table.HasCheckConstraint(
                    "ck_company_authentication_policies_idle_timeout",
                    $"idle_timeout_minutes BETWEEN {CompanyAuthenticationPolicy.MinimumIdleTimeoutMinutes} " +
                    $"AND {CompanyAuthenticationPolicy.MaximumIdleTimeoutMinutes}");

                table.HasCheckConstraint(
                    "ck_company_authentication_policies_absolute_lifetime",
                    $"absolute_lifetime_minutes BETWEEN {CompanyAuthenticationPolicy.MinimumAbsoluteLifetimeMinutes} " +
                    $"AND {CompanyAuthenticationPolicy.MaximumAbsoluteLifetimeMinutes}");

                // §3.2's rule as a constraint: an absolute lifetime shorter than the idle window
                // makes the idle window unreachable, and the setting reads as configured while
                // doing nothing.
                table.HasCheckConstraint(
                    "ck_company_authentication_policies_lifetime_order",
                    "absolute_lifetime_minutes >= idle_timeout_minutes");

                table.HasCheckConstraint(
                    "ck_company_authentication_policies_failed_attempts",
                    $"maximum_failed_attempts BETWEEN {CompanyAuthenticationPolicy.MinimumAllowedFailedAttempts} " +
                    $"AND {CompanyAuthenticationPolicy.MaximumAllowedFailedAttempts}");

                table.HasCheckConstraint(
                    "ck_company_authentication_policies_lockout_minutes",
                    $"lockout_minutes BETWEEN {CompanyAuthenticationPolicy.MinimumLockoutMinutes} " +
                    $"AND {CompanyAuthenticationPolicy.MaximumLockoutMinutes}");
            });

        // The Company is the key. A surrogate identifier would permit two policies for one
        // Company, which is a state nothing could resolve — neither row would be more current.
        builder.HasKey(policy => policy.CompanyId);
        builder.Property(policy => policy.CompanyId).ValueGeneratedNever();

        builder.Property(policy => policy.MinimumPasswordLength).IsRequired();
        builder.Property(policy => policy.RequireBreachCheck).IsRequired();
        builder.Property(policy => policy.IdleTimeoutMinutes).IsRequired();
        builder.Property(policy => policy.AbsoluteLifetimeMinutes).IsRequired();
        builder.Property(policy => policy.MfaRequired).IsRequired();
        builder.Property(policy => policy.MaximumFailedAttempts).IsRequired();
        builder.Property(policy => policy.LockoutMinutes).IsRequired();

        builder.Property(policy => policy.CreatedAtUtc).IsRequired();
        builder.Property(policy => policy.UpdatedAtUtc).IsRequired();
        builder.Property(policy => policy.UpdatedByEmployeeId);

        builder.Property(policy => policy.RowVersion).IsConcurrencyToken().IsRequired();
    }
}
