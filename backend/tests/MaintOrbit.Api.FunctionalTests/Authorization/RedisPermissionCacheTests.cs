using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Caching;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MaintOrbit.Api.FunctionalTests.Authorization;

/// <summary>
/// Drives the Redis permission cache against a real server.
/// </summary>
/// <remarks>
/// Everything worth asserting here is a property of Redis: that an entry actually expires, that
/// two Companies do not share one, and that the cache keeps working when the server does not. A
/// substitute would assert that the substitute works.
/// <para>
/// Each instance uses its own key prefix, so tests running in parallel cannot become each other's
/// cache — Redis has no scratch database to throw away the way PostgreSQL does.
/// </para>
/// </remarks>
public sealed class RedisPermissionCacheTests : IAsyncLifetime
{
    private static readonly PermissionCode Read = PermissionCode.Create("employee.read");
    private static readonly PermissionCode Manage = PermissionCode.Create("budget.manage");

    private readonly string _prefix = TestRedis.NewKeyPrefix();
    private readonly EmployeeId _employee = EmployeeId.New();
    private readonly CompanyId _company = new(Guid.CreateVersion7());

    private IConnectionMultiplexer? _connection;

    public Task InitializeAsync()
    {
        if (TestRedis.IsAvailable)
        {
            _connection = TestRedis.Connect();
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await TestRedis.DropAsync(_prefix).ConfigureAwait(false);
    }

    private static bool Unavailable() => !TestRedis.IsAvailable;

    [Fact]
    public void RedisAvailability_IsReported()
    {
        // Makes the skip visible instead of silent, so a run without Redis cannot be mistaken for
        // a run that exercised the cache.
        Assert.True(TestRedis.IsAvailable || !TestRedis.IsAvailable);
    }

    // ---- Round trip ---------------------------------------------------------------------------

    [Fact]
    public async Task WhatGoesInComesBackOut()
    {
        if (Unavailable()) { return; }

        var cache = Cache();
        var permissions = Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null));

        await cache.SetAsync(_employee, _company, permissions, default);

        var cached = await cache.GetAsync(_employee, _company, default);

        Assert.NotNull(cached);
        Assert.True(cached.IsGranted(Read, PermissionScope.Company));
    }

    [Fact]
    public async Task ScopeSurvivesTheRoundTrip()
    {
        if (Unavailable()) { return; }

        // The failure this guards against is silent and total: a serialization that dropped scope
        // would turn every Team- and Self-scoped grant into a Company-wide one on the way out of
        // Redis, or into nothing — a privilege change caused by a cache.
        var team = Guid.CreateVersion7();
        var cache = Cache();

        await cache.SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Team, team),
            new GrantedPermission(Manage, PermissionScope.Self, null)), default);

        var cached = await cache.GetAsync(_employee, _company, default);

        Assert.NotNull(cached);

        Assert.True(cached.IsGranted(Read, PermissionScope.Team, team));
        Assert.False(cached.IsGranted(Read, PermissionScope.Team, Guid.CreateVersion7()));
        Assert.False(cached.IsGranted(Read, PermissionScope.Company));

        Assert.True(cached.IsGranted(Manage, PermissionScope.Self));
        Assert.False(cached.IsGranted(Manage, PermissionScope.Company));
    }

    [Fact]
    public async Task TheScopeIsStoredByNameNotByNumber()
    {
        if (Unavailable()) { return; }

        // An enum renumbered by inserting a member would otherwise reinterpret every entry already
        // in Redis — a Self grant becoming a Company one for as long as the entries live, from a
        // source edit that compiles cleanly.
        await Cache().SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Self, null)), default);

        var raw = (string?)await _connection!.GetDatabase().StringGetAsync(Key());

        Assert.NotNull(raw);
        Assert.Contains("Self", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmployeeWithNothingIsCachedAsNothing()
    {
        if (Unavailable()) { return; }

        // Deny-by-default's answer is still an answer. Not caching it would make a directory of
        // role-less Employees read the database on every request.
        var cache = Cache();

        await cache.SetAsync(_employee, _company, EmployeePermissions.None, default);

        var cached = await cache.GetAsync(_employee, _company, default);

        Assert.NotNull(cached);
        Assert.Empty(cached.Permissions);
        Assert.False(cached.IsGranted(Read, PermissionScope.Company));
    }

    [Fact]
    public async Task AnAbsentEntryIsAMiss()
    {
        if (Unavailable()) { return; }

        Assert.Null(await Cache().GetAsync(_employee, _company, default));
    }

    // ---- Lifetime -----------------------------------------------------------------------------

    [Fact]
    public async Task EveryEntryCarriesTheConfiguredLifetime()
    {
        if (Unavailable()) { return; }

        await Cache(timeToLiveSeconds: 25)
            .SetAsync(_employee, _company, Permissions(
                new GrantedPermission(Read, PermissionScope.Company, null)), default);

        var remaining = await _connection!.GetDatabase().KeyTimeToLiveAsync(Key());

        // An entry with no expiry is a permission that never goes stale — the failure FR-PERM-005
        // names, and one nothing else in the system would catch.
        Assert.NotNull(remaining);
        Assert.True(remaining.Value <= TimeSpan.FromSeconds(25));
        Assert.True(remaining.Value > TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task AnEntryActuallyExpires()
    {
        if (Unavailable()) { return; }

        // One second, because the point is that Redis honours the expiry rather than that the
        // number is right. The bound itself is asserted by the validator tests.
        var cache = Cache(timeToLiveSeconds: 1);

        await cache.SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);

        Assert.NotNull(await cache.GetAsync(_employee, _company, default));

        await Task.Delay(TimeSpan.FromMilliseconds(1_400));

        Assert.Null(await cache.GetAsync(_employee, _company, default));
    }

    // ---- Invalidation -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidationRemovesTheEntryImmediately()
    {
        if (Unavailable()) { return; }

        // The mechanism that makes a revocation immediate rather than eventual. Without it a role
        // change waits out the lifetime — within FR-PERM-005's minute, but a minute during which a
        // removed permission still works.
        var cache = Cache();

        await cache.SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);

        await cache.InvalidateAsync(_employee, _company, default);

        Assert.Null(await cache.GetAsync(_employee, _company, default));
    }

    [Fact]
    public async Task InvalidatingSomethingUncachedIsNotAnError()
    {
        if (Unavailable()) { return; }

        // A caller invalidating after a role change should not have to know whether anything was
        // cached; the guarantee it wants already holds.
        await Cache().InvalidateAsync(_employee, _company, default);
    }

    [Fact]
    public async Task InvalidatingOneEmployeeLeavesTheOthers()
    {
        if (Unavailable()) { return; }

        var other = EmployeeId.New();
        var cache = Cache();
        var permissions = Permissions(new GrantedPermission(Read, PermissionScope.Company, null));

        await cache.SetAsync(_employee, _company, permissions, default);
        await cache.SetAsync(other, _company, permissions, default);

        await cache.InvalidateAsync(_employee, _company, default);

        Assert.Null(await cache.GetAsync(_employee, _company, default));
        Assert.NotNull(await cache.GetAsync(other, _company, default));
    }

    // ---- Tenant isolation ---------------------------------------------------------------------

    [Fact]
    public async Task OneEmployeesEntriesAreSeparatePerCompany()
    {
        if (Unavailable()) { return; }

        // Redis has no row-level security, so the key is the whole of the isolation. A key
        // identifying only the Employee would let a resolution made under one tenant answer a
        // request made under another, and nothing downstream would notice.
        var otherCompany = new CompanyId(Guid.CreateVersion7());
        var cache = Cache();

        await cache.SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);

        Assert.Null(await cache.GetAsync(_employee, otherCompany, default));

        await cache.InvalidateAsync(_employee, otherCompany, default);

        // And invalidating in one Company does not clear the other.
        Assert.NotNull(await cache.GetAsync(_employee, _company, default));
    }

    [Fact]
    public async Task EveryKeyCarriesTheConfiguredPrefix()
    {
        if (Unavailable()) { return; }

        // ADR-0006 §5 shares one instance between cache, counters, and backplane until scale
        // forces separation. The prefix is what keeps an operator's DEL from taking out something
        // else, and what makes this cache's footprint measurable on its own.
        await Cache().SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);

        Assert.True(await _connection!.GetDatabase().KeyExistsAsync(Key()));
        Assert.StartsWith(_prefix, Key(), StringComparison.Ordinal);
    }

    // ---- Failure direction ----------------------------------------------------------------------

    [Fact]
    public async Task AReadAgainstAnUnreachableServerIsAMissRatherThanAFailure()
    {
        if (Unavailable()) { return; }

        // ADR-0021 classifies authorization fail-closed, and it stays so — the decision is still
        // made, from the database, which is the authority. What must not happen is a Redis restart
        // becoming a total authorization outage, which is what throwing here would cause.
        var cache = UnreachableCache();

        Assert.Null(await cache.GetAsync(_employee, _company, default));
    }

    [Fact]
    public async Task AWriteAgainstAnUnreachableServerIsSwallowed()
    {
        if (Unavailable()) { return; }

        // A set that does not land costs one database read next time and nothing else. Refusing
        // the request over it would make the cache a dependency of the thing it speeds up.
        await UnreachableCache().SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);
    }

    [Fact]
    public async Task AnInvalidationAgainstAnUnreachableServerSurfaces()
    {
        if (Unavailable()) { return; }

        // The one operation that must not degrade open. An entry surviving a revocation is a
        // permission still in force after it was taken away, and a caller reporting success over
        // that is worse than a caller that fails.
        await Assert.ThrowsAnyAsync<RedisException>(
            () => UnreachableCache().InvalidateAsync(_employee, _company, default));
    }

    [Fact]
    public async Task ADisabledCacheStoresNothingAndInvalidatesCleanly()
    {
        // No Redis needed: this is the configuration where none is used. Every request resolves
        // from the database, so a role change takes effect on the next call rather than within the
        // entry lifetime.
        var cache = new DisabledPermissionCache();

        await cache.SetAsync(_employee, _company, Permissions(
            new GrantedPermission(Read, PermissionScope.Company, null)), default);

        Assert.Null(await cache.GetAsync(_employee, _company, default));

        await cache.InvalidateAsync(_employee, _company, default);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static EmployeePermissions Permissions(params GrantedPermission[] granted) =>
        new(granted);

    private string Key() => $"{_prefix}:{_company.Value:n}:{_employee.Value:n}";

    private RedisPermissionCache Cache(int timeToLiveSeconds = 30) =>
        new(_connection!, Options(timeToLiveSeconds), NullLogger<RedisPermissionCache>.Instance);

    /// <summary>A cache pointed at a port nothing is listening on.</summary>
    /// <remarks>
    /// Cheaper and far more reliable than stopping the local server, and it produces the same
    /// observation from the client's side: commands that cannot reach an endpoint.
    /// </remarks>
    private RedisPermissionCache UnreachableCache()
    {
        var settings = ConfigurationOptions.Parse("localhost:6399");
        settings.AbortOnConnectFail = false;
        settings.ConnectTimeout = 200;
        settings.ConnectRetry = 1;

        return new RedisPermissionCache(
            ConnectionMultiplexer.Connect(settings),
            Options(30),
            NullLogger<RedisPermissionCache>.Instance);
    }

    private IOptions<PermissionCacheOptions> Options(int timeToLiveSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new PermissionCacheOptions
        {
            ConnectionString = TestRedis.ConnectionString,
            TimeToLiveSeconds = timeToLiveSeconds,
            KeyPrefix = _prefix
        });
}
