using System.Text.Json;
using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MaintOrbit.Infrastructure.Caching;

/// <summary>
/// The Redis-backed permission cache (ADR-0006).
/// </summary>
/// <remarks>
/// <b>Shared, not per-process, and that is the whole reason it is Redis.</b> A per-host dictionary
/// would be wrong in a way that only appears under load: with more than one API host, an
/// invalidation on one leaves the others serving a revoked role until their own entry lapses, and
/// FR-PERM-005 fails silently on some fraction of requests.
/// <para>
/// <b>Failure direction is decided per operation, not per call site</b> — ADR-0021's rule, and the
/// three operations do not share an answer:
/// </para>
/// <list type="bullet">
/// <item><b>Read</b> degrades open. A miss and an outage are the same observation to the caller —
/// resolve from the database, which is the authority. ADR-0021 classifies <i>authorization</i>
/// fail-closed, and it stays so: the decision is still made, from the authoritative source. Making
/// the cache fail closed would turn a Redis restart into a total authorization outage while adding
/// nothing, because the answer was never Redis's to give.</item>
/// <item><b>Write</b> degrades open. A set that does not land costs a database read next time and
/// nothing else.</item>
/// <item><b>Invalidation</b> does <b>not</b> degrade open, and is the one that must not. An entry
/// that survives a revocation is a permission still in force after it was taken away, which is the
/// exact failure FR-PERM-005 exists to prevent. It surfaces, so a caller can fail the operation
/// that asked for it rather than report success over a stale grant.</item>
/// </list>
/// <para>
/// The entry lifetime is what makes even a missed invalidation bounded: §3.7 lists the
/// time-to-live ceiling as the mechanism that "never" fails, and
/// <see cref="PermissionCacheOptions"/> holds it strictly under sixty seconds.
/// </para>
/// </remarks>
internal sealed partial class RedisPermissionCache(
    IConnectionMultiplexer connection,
    IOptions<PermissionCacheOptions> options,
    ILogger<RedisPermissionCache> logger)
    : IPermissionCache
{
    /// <summary>Compact and stable, so an entry written by one build is readable by the next.</summary>
    /// <remarks>
    /// The scope is written as a name rather than a number. An enum renumbered by inserting a
    /// member would otherwise reinterpret every entry already in Redis — turning a Self grant into
    /// a Company one for as long as the entries live, which is a privilege escalation caused by a
    /// source edit that compiles cleanly.
    /// </remarks>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public async Task<EmployeePermissions?> GetAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken)
    {
        try
        {
            var value = await connection.GetDatabase()
                .StringGetAsync(Key(employeeId, companyId))
                .ConfigureAwait(false);

            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<CachedPermissions>((string)value!, Json);

            return entry is null ? null : Rehydrate(entry);
        }
        catch (Exception error) when (IsCacheFault(error))
        {
            // Degrade open: the caller resolves from the database, which is where the answer
            // actually lives. Logged rather than swallowed, because a cache that is quietly always
            // missing looks exactly like one that is working and is merely slow.
            CacheUnavailable(logger, error);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        EmployeeId employeeId,
        CompanyId companyId,
        EmployeePermissions permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        try
        {
            await connection.GetDatabase()
                .StringSetAsync(
                    Key(employeeId, companyId),
                    JsonSerializer.Serialize(Dehydrate(permissions), Json),
                    TimeSpan.FromSeconds(options.Value.TimeToLiveSeconds))
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheFault(error))
        {
            // A set that does not land costs one database read next time. Refusing the request
            // over it would make the cache a dependency of the thing it exists to speed up.
            CacheUnavailable(logger, error);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deliberately not wrapped.</b> Every other operation here degrades open; this one cannot,
    /// because the failure it would hide is a revoked permission still granting access. The caller
    /// — a role assignment or removal — must be able to fail rather than report success over an
    /// entry that survived.
    /// <para>
    /// It is still bounded even if a caller ignores it: the entry expires under sixty seconds
    /// regardless, which is the guarantee FR-PERM-005 actually asks for.
    /// </para>
    /// </remarks>
    public async Task InvalidateAsync(
        EmployeeId employeeId, CompanyId companyId, CancellationToken cancellationToken)
    {
        try
        {
            await connection.GetDatabase()
                .KeyDeleteAsync(Key(employeeId, companyId))
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheFault(error))
        {
            InvalidationFailed(logger, employeeId.ToString(), error);
            throw;
        }
    }

    /// <summary>
    /// The key for one Employee's permissions in one Company.
    /// </summary>
    /// <remarks>
    /// <b>The Company is part of the key, not a detail of the value.</b> Permissions are resolved
    /// per Company, and a key that identified only the Employee would let a resolution made under
    /// one tenant answer a request made under another — a cross-tenant leak that row-level
    /// security could not see, because Redis has no policies.
    /// </remarks>
    private RedisKey Key(EmployeeId employeeId, CompanyId companyId) =>
        $"{options.Value.KeyPrefix}:{companyId.Value:n}:{employeeId.Value:n}";

    /// <summary>
    /// Whether an exception is the cache being unavailable rather than a defect.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Catching everything would swallow a serialization bug as though it were
    /// an outage, and the symptom would be a cache that silently never hits.
    /// </remarks>
    private static bool IsCacheFault(Exception error) =>
        error is RedisException or ObjectDisposedException or TimeoutException;

    /// <summary>The wire shape. Flat, so it survives a change to the in-memory type's internals.</summary>
    private sealed record CachedPermissions(IReadOnlyList<CachedGrant> Grants);

    private sealed record CachedGrant(string Permission, PermissionScope Scope, Guid? ScopeId);

    private static CachedPermissions Dehydrate(EmployeePermissions permissions) =>
        new([.. permissions.Grants.Select(grant =>
            new CachedGrant(grant.Permission.Value, grant.Scope, grant.ScopeId))]);

    private static EmployeePermissions Rehydrate(CachedPermissions entry) =>
        new(entry.Grants.Select(grant =>
            new GrantedPermission(
                PermissionCode.Create(grant.Permission), grant.Scope, grant.ScopeId)));

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "The permission cache is unavailable; resolving from the database instead.")]
    private static partial void CacheUnavailable(ILogger logger, Exception error);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Error,
        Message = "Failed to invalidate the permission cache for Employee {EmployeeId}. Their " +
                  "entry remains until it expires, so a revoked grant may still be honoured " +
                  "until then.")]
    private static partial void InvalidationFailed(
        ILogger logger, string employeeId, Exception error);
}
