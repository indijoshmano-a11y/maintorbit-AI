using Microsoft.Extensions.Options;
using Npgsql;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// Rejects database settings that would fail late or unsafely.
/// </summary>
public sealed class PersistenceOptionsValidator : IValidateOptions<PersistenceOptions>
{
    public ValidateOptionsResult Validate(string? name, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateConnectionString(options.ConnectionString, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateConnectionString(string connectionString, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // DataAnnotations already reports the empty case; returning here keeps a missing
            // connection string from also producing a parse failure for the same mistake.
            return;
        }

        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            // Deliberately reports only that parsing failed. The string being parsed contains
            // the database password, and an options validation failure is written to the log
            // (LG-2, NFR-OBS-009).
            failures.Add(
                $"Persistence:ConnectionString is not a valid Npgsql connection string: {exception.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            failures.Add("Persistence:ConnectionString names no Host.");
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            failures.Add("Persistence:ConnectionString names no Database.");
        }

        if (builder.Multiplexing)
        {
            // Multiplexing interleaves commands from different logical operations over one
            // physical connection, so per-connection session state stops being per-operation.
            // Every pooling mode §6.7 assesses as viable depends on that state holding for the
            // duration of a transaction, so this is unsafe under all of them — it does not
            // wait on DD-2.
            failures.Add(
                "Persistence:ConnectionString enables Multiplexing, which is incompatible with " +
                "session-scoped row-level security: session state cannot be relied upon for " +
                "the duration of an operation, and the tenant variable is session state.");
        }
    }
}
