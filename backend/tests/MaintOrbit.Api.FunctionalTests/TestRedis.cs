using StackExchange.Redis;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// Reaches a local Redis for tests, when one is running.
/// </summary>
/// <remarks>
/// The same shape as <see cref="TestDatabase"/> and for the same reason: the permission cache is
/// only interesting against a real server. Entry lifetime, key isolation, and behaviour when the
/// server goes away are all properties of Redis, and a substitute would assert that the substitute
/// works.
/// <para>
/// <b>Skipped when none is reachable</b> rather than failing, so the suite still runs where Redis
/// is not installed. Docker is unavailable here, so <c>Testcontainers.Redis</c> — which
/// backend-technologies §11 lists for exactly this — cannot be used yet; when it can, this helper
/// is what it replaces.
/// </para>
/// <para>
/// Every test gets its own key prefix. Redis has no databases to throw away the way PostgreSQL
/// does, so isolation has to come from the keyspace, and a shared prefix would make two tests
/// running in parallel each other's cache.
/// </para>
/// </remarks>
internal static class TestRedis
{
    /// <summary>Where a local Redis is expected.</summary>
    public const string ConnectionString = "localhost:6379";

    /// <summary>Whether a Redis is reachable, evaluated once for the assembly.</summary>
    public static bool IsAvailable { get; } = Probe();

    /// <summary>A key prefix no other test uses.</summary>
    public static string NewKeyPrefix() => $"maintorbit:test:{Guid.CreateVersion7():n}"[..40];

    /// <summary>Opens a connection for a test to inspect what the cache wrote.</summary>
    public static IConnectionMultiplexer Connect()
    {
        var settings = ConfigurationOptions.Parse(ConnectionString);
        settings.AbortOnConnectFail = false;

        return ConnectionMultiplexer.Connect(settings);
    }

    /// <summary>Removes every key a test wrote, so a run leaves the keyspace as it found it.</summary>
    public static async Task DropAsync(string keyPrefix)
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            using var connection = Connect();
            var database = connection.GetDatabase();

            foreach (var endpoint in connection.GetEndPoints())
            {
                var server = connection.GetServer(endpoint);

                // KEYS rather than SCAN: this is a scratch keyspace of at most a handful of
                // entries, and the pattern is unique to one test. On a production keyspace the
                // choice would be the other way round.
                foreach (var key in server.Keys(pattern: $"{keyPrefix}*"))
                {
                    await database.KeyDeleteAsync(key).ConfigureAwait(false);
                }
            }
        }
        catch (RedisException)
        {
            // A scratch keyspace outliving a failed run is untidy, not unsafe.
        }
    }

    private static bool Probe()
    {
        try
        {
            var settings = ConfigurationOptions.Parse(ConnectionString);
            settings.AbortOnConnectFail = false;
            settings.ConnectTimeout = 1_000;

            using var connection = ConnectionMultiplexer.Connect(settings);

            return connection.IsConnected;
        }
        catch (RedisException)
        {
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
