using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="MaintOrbitDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// Without this, the tooling starts the API host to obtain a context — which means running the
/// composition root, its configuration validation, and every registration in it just to read
/// the model. A factory keeps <c>migrations add</c> a local, offline operation that does not
/// depend on the host being startable.
/// <para>
/// No connection is opened. Generating a migration reads the model and compares it to the
/// previous snapshot; a reachable database is needed only to <i>apply</i> one.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MaintOrbitDbContext>
{
    /// <summary>
    /// Environment variable supplying a connection string for commands that reach the database.
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a configuration file: the value carries a password,
    /// and design-time tooling runs on developer machines and build agents where a committed
    /// file would be the wrong place for one (CF-3).
    /// </remarks>
    public const string ConnectionStringVariable = "MAINTORBIT_MIGRATIONS_CONNECTION";

    /// <summary>
    /// Placeholder used when the variable is unset.
    /// </summary>
    /// <remarks>
    /// Structurally valid so the provider can be configured, and deliberately pointed at
    /// nothing real. <c>migrations add</c> and <c>migrations script</c> work with it;
    /// <c>database update</c> fails to connect, which is the correct outcome for a command that
    /// was told nothing about where the database is.
    /// </remarks>
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=maintorbit_design_time;Username=maintorbit";

    public MaintOrbitDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } supplied
                ? supplied
                : PlaceholderConnectionString;

        var builder = new DbContextOptionsBuilder<MaintOrbitDbContext>();

        // The same configuration the application runs with, so a migration cannot be generated
        // against settings that differ from the ones in production.
        NpgsqlConfiguration.Apply(builder, new PersistenceOptions
        {
            ConnectionString = connectionString
        });

        return new MaintOrbitDbContext(builder.Options);
    }
}
