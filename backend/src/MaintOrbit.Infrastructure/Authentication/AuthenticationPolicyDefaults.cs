using System.ComponentModel.DataAnnotations;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>The authentication policy a Company has until it sets its own.</summary>
/// <remarks>
/// FR-AUTH-002, FR-AUTH-006, FR-AUTH-007, and FR-AUTH-011 all make their settings
/// Company-configured. Configuration here is what applies before a Company has configured
/// anything — the deployment's opinion, not a second policy model.
/// <para>
/// <b>Every bound is the aggregate's, restated.</b> The ranges below match
/// <see cref="CompanyAuthenticationPolicy"/>'s constants, and the validator checks the whole set
/// through the aggregate itself rather than re-deriving the rules. Two sets of bounds that drifted
/// would make a deployment able to default to a policy no Company could save.
/// </para>
/// </remarks>
public sealed class AuthenticationPolicyDefaults
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "AuthenticationPolicy";

    /// <summary>Shortest password accepted (FR-AUTH-002).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAllowedPasswordLength,
        CompanyAuthenticationPolicy.MaximumAllowedPasswordLength)]
    public int MinimumPasswordLength { get; init; } =
        CompanyAuthenticationPolicy.MinimumAllowedPasswordLength;

    /// <summary>Whether new passwords are checked against breach corpora (FR-AUTH-002).</summary>
    public bool RequireBreachCheck { get; init; } = true;

    /// <summary>Idle window (FR-AUTH-007).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumIdleTimeoutMinutes,
        CompanyAuthenticationPolicy.MaximumIdleTimeoutMinutes)]
    public int IdleTimeoutMinutes { get; init; } = 60;

    /// <summary>Absolute lifetime (FR-AUTH-007).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAbsoluteLifetimeMinutes,
        CompanyAuthenticationPolicy.MaximumAbsoluteLifetimeMinutes)]
    public int AbsoluteLifetimeMinutes { get; init; } = 720;

    /// <summary>
    /// Whether a second factor is required by default (FR-AUTH-006).
    /// </summary>
    /// <remarks>
    /// False. A deployment-wide default of true would require every Employee to enrol before they
    /// could do anything, including the first administrator of a new Company — who would have
    /// nobody to turn it off for them.
    /// </remarks>
    public bool MfaRequired { get; init; }

    /// <summary>Consecutive failures before lockout (FR-AUTH-011).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAllowedFailedAttempts,
        CompanyAuthenticationPolicy.MaximumAllowedFailedAttempts)]
    public int MaximumFailedAttempts { get; init; } = 5;

    /// <summary>How long a lockout lasts.</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumLockoutMinutes,
        CompanyAuthenticationPolicy.MaximumLockoutMinutes)]
    public int LockoutMinutes { get; init; } = 15;
}

/// <summary>Validates the defaults at startup, through the aggregate that will enforce them.</summary>
/// <remarks>
/// <b>It builds a policy rather than re-checking the numbers.</b> The attributes above catch each
/// field in isolation; the relational rule — an absolute lifetime not shorter than the idle window
/// — belongs to the aggregate, and asking the aggregate is what stops the two from disagreeing.
/// <para>
/// Checked on start rather than at first use, so a deployment configured with an unusable default
/// refuses to run instead of failing on somebody's first sign-in.
/// </para>
/// </remarks>
internal sealed class AuthenticationPolicyDefaultsValidator
    : IValidateOptions<AuthenticationPolicyDefaults>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationPolicyDefaults options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var candidate = CompanyAuthenticationPolicy.Create(
            new CompanyId(Guid.CreateVersion7()),
            options.MinimumPasswordLength,
            options.RequireBreachCheck,
            options.IdleTimeoutMinutes,
            options.AbsoluteLifetimeMinutes,
            options.MfaRequired,
            options.MaximumFailedAttempts,
            options.LockoutMinutes,
            DateTimeOffset.UnixEpoch);

        return candidate.IsSuccess
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AuthenticationPolicyDefaults.SectionName}: {candidate.Error.Description} " +
                "A deployment default must itself be a policy a Company could save.");
    }
}
