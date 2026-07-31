using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MaintOrbit.Infrastructure.Persistence.Conventions;

/// <summary>
/// Applies the documented database naming conventions to the whole model.
/// </summary>
/// <remarks>
/// <c>docs/06-database/database-design.md</c> §1.5 and coding-standards S-1 specify
/// <c>snake_case</c> tables and columns, plural table names, and <c>ix_</c>/<c>ux_</c> index
/// prefixes. EF Core's defaults are PascalCase, so without this every table and column would
/// have to be renamed by hand in its configuration — which works until the first one is
/// forgotten, and a forgotten rename is invisible until someone reads the schema.
/// <para>
/// Written here rather than taken from <c>EFCore.NamingConventions</c>, which is the usual
/// answer: that package is not listed in <c>docs/04-technology/backend-technologies.md</c>,
/// and the dependency policy gates additions. The rules below are small enough to own.
/// </para>
/// </remarks>
public static class NamingConventions
{
    /// <summary>
    /// Rewrites every table, column, key, foreign key, and index name in the model.
    /// </summary>
    /// <remarks>
    /// Runs after entity configurations are applied, so an explicit name set by a
    /// configuration is converted rather than overridden — <c>ProviderConnections</c> becomes
    /// <c>provider_connections</c> whether the name was inferred or written down.
    /// </remarks>
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            ApplyTo(entityType);
        }
    }

    private static void ApplyTo(IMutableEntityType entityType)
    {
        var tableName = entityType.GetTableName();

        if (tableName is not null)
        {
            entityType.SetTableName(ToSnakeCase(tableName));
        }

        foreach (var property in entityType.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.GetColumnName()));
        }

        foreach (var key in entityType.GetKeys())
        {
            // EF names these PK_Thing / AK_Thing_Column; converting preserves the prefix
            // convention while matching §1.5's casing.
            var name = key.GetName();
            if (name is not null)
            {
                key.SetName(ToSnakeCase(name));
            }
        }

        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            var name = foreignKey.GetConstraintName();
            if (name is not null)
            {
                foreignKey.SetConstraintName(ToSnakeCase(name));
            }
        }

        foreach (var index in entityType.GetIndexes())
        {
            index.SetDatabaseName(IndexName(entityType, index));
        }
    }

    /// <summary>
    /// Builds an index name as <c>ix_&lt;table&gt;_&lt;columns&gt;</c>, or <c>ux_</c> when unique.
    /// </summary>
    /// <remarks>
    /// §1.5 gives both prefixes and the column-suffixed form. Naming an index after the columns
    /// it covers is what makes a duplicate index obvious in a schema listing rather than
    /// something discovered later from a write-throughput regression.
    /// </remarks>
    private static string IndexName(IMutableEntityType entityType, IMutableIndex index)
    {
        var table = ToSnakeCase(entityType.GetTableName() ?? entityType.ShortName());
        var columns = index.Properties.Select(static property => ToSnakeCase(property.Name));

        var prefix = index.IsUnique ? "ux" : "ix";

        return $"{prefix}_{table}_{string.Join('_', columns)}";
    }

    /// <summary>
    /// Converts a PascalCase or camelCase identifier to <c>snake_case</c>.
    /// </summary>
    /// <remarks>
    /// Boundaries are inserted before an upper-case letter that follows a lower-case letter or
    /// digit, and before the last upper-case letter of a run that is followed by a lower-case
    /// one. That second rule is what keeps an acronym intact: <c>APIKeyHash</c> becomes
    /// <c>api_key_hash</c> rather than <c>a_p_i_key_hash</c>.
    /// </remarks>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];

            if (current == '_')
            {
                AppendSeparator(builder);
                continue;
            }

            if (char.IsUpper(current) && index > 0 && NeedsSeparatorBefore(name, index))
            {
                AppendSeparator(builder);
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static bool NeedsSeparatorBefore(string name, int index)
    {
        var previous = name[index - 1];

        if (char.IsLower(previous) || char.IsDigit(previous))
        {
            return true;
        }

        // End of an acronym run: the upper-case letter that begins the next word.
        return char.IsUpper(previous)
               && index + 1 < name.Length
               && char.IsLower(name[index + 1]);
    }

    /// <summary>
    /// Appends a separator unless one is already there, so <c>PK_Thing</c> does not become
    /// <c>pk__thing</c>.
    /// </summary>
    private static void AppendSeparator(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '_')
        {
            builder.Append('_');
        }
    }
}
