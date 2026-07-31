using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Conventions;

/// <summary>
/// Enforces the one-schema-per-module rule structurally.
/// </summary>
/// <remarks>
/// DB-P2 requires one schema per module, and ADR-0002 makes the module boundary the thing that
/// keeps later service extraction possible. EF Core's default is to place a table in the
/// provider's default schema — <c>public</c> for PostgreSQL — when a configuration does not say
/// otherwise. That default is silent: a table lands in the wrong place, nothing fails, and the
/// mistake is found when someone reads the schema or, worse, when extraction is attempted.
/// <para>
/// No default schema is set on the model, deliberately. Setting one would make the omission
/// invisible rather than impossible.
/// </para>
/// </remarks>
public static class SchemaConventions
{
    /// <summary>
    /// Throws when any mapped entity type has no explicit schema.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// One or more entity types are mapped to a table without naming a schema.
    /// </exception>
    public static void RequireExplicitSchema(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var unassigned = modelBuilder.Model.GetEntityTypes()
            .Where(static entityType => entityType.GetTableName() is not null)
            .Where(static entityType => string.IsNullOrEmpty(entityType.GetSchema()))
            .Select(static entityType => entityType.DisplayName())
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unassigned.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Every entity type must name the module schema that owns it (DB-P2). " +
            $"Missing a schema: {string.Join(", ", unassigned)}. " +
            "Set it in the entity's IEntityTypeConfiguration with ToTable(name, schema).");
    }
}
