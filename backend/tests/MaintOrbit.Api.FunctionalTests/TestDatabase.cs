using Npgsql;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// Creates a throwaway PostgreSQL database for end-to-end tests, when one is reachable.
/// </summary>
/// <remarks>
/// Sign-in resolves a Company across tenants, opens a scope, verifies an Argon2id hash, and writes
/// a session and a refresh token. That chain only means anything against row-level security, so
/// these tests need a real database rather than a substitute.
/// <para>
/// <b>They are skipped when none is reachable</b> rather than failing, so the suite still runs
/// where PostgreSQL is not installed. Docker is unavailable here, so Testcontainers — which
/// backend-technologies §11 lists for exactly this — cannot be used yet; when it can, this helper
/// is what it replaces.
/// </para>
/// </remarks>
internal static class TestDatabase
{
    private static string Administrative =>
        $"Host=localhost;Port=5432;Database=postgres;Username={Owner}";

    /// <summary>
    /// A fresh name per call.
    /// </summary>
    /// <remarks>
    /// <b>Per call, not per assembly.</b> A single shared name looks harmless while one test class
    /// uses it and turns into two classes creating and dropping the same database underneath each
    /// other the moment a second one appears — which surfaces as unrelated connection failures
    /// scattered across the suite rather than as a collision.
    /// </remarks>
    private static string NewDatabaseName() =>
        $"maintorbit_e2e_{Guid.CreateVersion7():n}"[..40];

    private static string Owner => Environment.UserName;

    /// <summary>Creates the database, or returns null when PostgreSQL is unreachable.</summary>
    public static async Task<string?> CreateAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Administrative);

            await connection.OpenAsync().ConfigureAwait(false);

            var databaseName = NewDatabaseName();

            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE {databaseName}", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            return $"Host=localhost;Port=5432;Database={databaseName};Username={Owner}";
        }
        catch (NpgsqlException)
        {
            // No server, no permission, or no such role. Either way these tests cannot run, and
            // saying so is better than failing a suite for an absent dependency.
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the database the given connection string names, ignoring failure.
    /// </summary>
    /// <remarks>
    /// Takes the connection string rather than reading a shared field, so a class can only ever
    /// drop the database it created. Null is the "no server was reachable" case and does nothing.
    /// </remarks>
    public static async Task DropAsync(string? connectionString)
    {
        if (connectionString is null)
        {
            return;
        }

        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;

        if (string.IsNullOrEmpty(databaseName))
        {
            return;
        }

        try
        {
            NpgsqlConnection.ClearAllPools();

            await using var connection = new NpgsqlConnection(Administrative);

            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Nothing to clean up, or nothing that can be.
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
    }
}
