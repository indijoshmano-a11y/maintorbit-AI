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

    private static readonly string DatabaseName =
        $"maintorbit_e2e_{Guid.CreateVersion7():n}"[..40];

    private static string Owner => Environment.UserName;

    /// <summary>Creates the database, or returns null when PostgreSQL is unreachable.</summary>
    public static async Task<string?> CreateAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Administrative);

            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE {DatabaseName}", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            return $"Host=localhost;Port=5432;Database={DatabaseName};Username={Owner}";
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

    /// <summary>Drops the database, ignoring failure — it is a scratch artefact.</summary>
    public static async Task DropAsync()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();

            await using var connection = new NpgsqlConnection(Administrative);

            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)", connection);
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
