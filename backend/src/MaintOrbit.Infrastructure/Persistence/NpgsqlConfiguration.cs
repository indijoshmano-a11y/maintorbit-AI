using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// The single place the PostgreSQL provider is configured.
/// </summary>
/// <remarks>
/// Shared by the runtime registration and the design-time factory on purpose. If the two
/// configured the provider separately they would drift, and the symptom of that drift is a
/// migration generated against settings the application does not run with — which is only
/// discovered when the migration is applied.
/// </remarks>
internal static class NpgsqlConfiguration
{
    /// <summary>
    /// Table recording which migrations have been applied.
    /// </summary>
    /// <remarks>
    /// Named in <c>snake_case</c> for consistency with §1.5 rather than left as EF's
    /// <c>__EFMigrationsHistory</c>, which would be the one PascalCase identifier in a
    /// snake_case schema. The leading underscores are kept: they are what marks it as
    /// infrastructure rather than application data.
    /// </remarks>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Schema holding the migrations history table.
    /// </summary>
    /// <remarks>
    /// <c>public</c>, not a module schema. The twelve schemas in §2 each belong to a module;
    /// migration history belongs to none of them, and inventing a thirteenth schema to hold one
    /// infrastructure table would add a name to a list the documentation defines.
    /// </remarks>
    public const string MigrationsHistorySchema = "public";

    /// <summary>
    /// Applies the PostgreSQL provider and its settings.
    /// </summary>
    public static void Apply(DbContextOptionsBuilder builder, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.UseNpgsql(options.ConnectionString, npgsql =>
        {
            // Migrations live beside the DbContext, so `dotnet ef` needs no --msbuildprojectextensionspath
            // gymnastics and a migration cannot end up in the API host by accident.
            npgsql.MigrationsAssembly(typeof(MaintOrbitDbContext).Assembly.FullName);
            npgsql.MigrationsHistoryTable(MigrationsHistoryTable, MigrationsHistorySchema);
            npgsql.CommandTimeout(options.CommandTimeoutSeconds);

            if (options.MaxRetryAttempts > 0)
            {
                npgsql.EnableRetryOnFailure(
                    options.MaxRetryAttempts,
                    TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                    errorCodesToAdd: null);
            }
        });
    }
}
