using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MaintOrbit.Infrastructure.Caching;

/// <summary>Permission cache settings.</summary>
/// <remarks>
/// ADR-0006 selects Redis for the cache role, and 02-authentication-architecture §3.6 resolves
/// permissions "server-side per request from cache". What the cache may not do is outlive
/// FR-PERM-005's 60-second bound on a role change taking effect — so the lifetime is not a tuning
/// knob with a wide range, it is a security parameter with a ceiling.
/// </remarks>
public sealed class PermissionCacheOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "PermissionCache";

    /// <summary>
    /// The hard ceiling on entry lifetime, in seconds.
    /// </summary>
    /// <remarks>
    /// FR-PERM-005: "role changes take effect within one minute".
    /// 02-authentication-architecture §3.7 lists the time-to-live ceiling as the mechanism that
    /// "never" fails — the other two, invalidation and tombstones, can be missed. Sixty is
    /// therefore the requirement itself, and the range below stops strictly short of it: a
    /// lifetime <i>equal</i> to the bound satisfies it only if nothing else costs a millisecond.
    /// </remarks>
    public const int MaximumTimeToLiveSeconds = 60;

    /// <summary>
    /// The Redis connection string. Empty disables the cache.
    /// </summary>
    /// <remarks>
    /// <b>Empty is a supported configuration, not a broken one.</b> With no cache every request
    /// resolves from the database — slower, and immediately correct, which is the safe direction
    /// to degrade in. Making Redis mandatory would turn a cache outage into a total authorization
    /// outage for no security gain, since the database holds the answer either way.
    /// <para>
    /// Never a literal in a committed file. Credentials come from the environment or a mounted
    /// secret (backend-technologies §5.2: "credentials from configuration, never source").
    /// </para>
    /// </remarks>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// How long a resolved permission set may be reused.
    /// </summary>
    /// <remarks>
    /// Thirty seconds by default — half the ceiling, so a deployment has room to raise it without
    /// reaching the bound. The trade is plain: a longer lifetime means fewer database reads and a
    /// longer window in which a revoked role still works.
    /// </remarks>
    [Range(1, MaximumTimeToLiveSeconds - 1)]
    public int TimeToLiveSeconds { get; init; } = 30;

    /// <summary>
    /// Prefix for every key this cache writes.
    /// </summary>
    /// <remarks>
    /// ADR-0006 §5 shares one instance between cache, counters, and backplane until scale forces
    /// separation, so keys from different roles sit in the same keyspace. A prefix is what keeps
    /// an operator's <c>DEL</c> from taking out something else, and what makes the cache's
    /// footprint measurable on its own.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string KeyPrefix { get; init; } = "maintorbit:perm";

    /// <summary>Whether a cache is configured at all.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>Validates the cache settings at startup.</summary>
/// <remarks>
/// Checked on start rather than at first use, so a deployment that would silently exceed
/// FR-PERM-005's bound refuses to run instead of serving stale grants nobody notices. Same
/// fail-fast posture as the signing key and the data encryption key.
/// </remarks>
internal sealed class PermissionCacheOptionsValidator : IValidateOptions<PermissionCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, PermissionCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Restated rather than left to the Range attribute. The attribute is the enforcement; this
        // is the one that names the requirement, so a failed startup says why the number matters
        // instead of quoting a range.
        if (options.TimeToLiveSeconds >= PermissionCacheOptions.MaximumTimeToLiveSeconds)
        {
            return ValidateOptionsResult.Fail(
                $"{PermissionCacheOptions.SectionName}:{nameof(PermissionCacheOptions.TimeToLiveSeconds)} " +
                $"must be under {PermissionCacheOptions.MaximumTimeToLiveSeconds} seconds. " +
                "FR-PERM-005 requires a role change to take effect within one minute, and the " +
                "entry lifetime is the bound that holds when invalidation is missed.");
        }

        if (!options.IsEnabled)
        {
            // No cache configured. Every request resolves from the database, which is correct and
            // needs no further settings — validating a connection string that is deliberately
            // absent would make the safe configuration the one that fails to start.
            return ValidateOptionsResult.Success;
        }

        try
        {
            // Parsed, not connected. A malformed string is a configuration error and should stop
            // startup; an unreachable server is an outage the cache is built to survive, and
            // refusing to start for it would make Redis a hard dependency of authorization.
            ConfigurationOptions.Parse(options.ConnectionString);
        }
        catch (ArgumentException error)
        {
            return ValidateOptionsResult.Fail(
                $"{PermissionCacheOptions.SectionName}:{nameof(PermissionCacheOptions.ConnectionString)} " +
                $"is not a valid Redis configuration: {error.Message}");
        }

        return ValidateOptionsResult.Success;
    }
}
