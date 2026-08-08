using System.Text.RegularExpressions;

namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Rules that keep the authoritative documentation describing the system that actually exists.
/// </summary>
/// <remarks>
/// Phase 11 delivered eleven milestones of working identity infrastructure and, along the way,
/// accumulated six documentation defects spanning nine tables that no longer had a definition
/// anywhere. Each was found by a person reading a document while implementing against it. That is
/// the expensive way to find them, and it only works while somebody is still reading.
/// <para>
/// These rules make the cheap failure automatic. They are deliberately <b>one-directional</b>: a
/// table that exists must be documented, and a package the build references must be listed. Neither
/// requires the reverse, because <c>06-database</c> and <c>04-technology</c> are forward-looking
/// specifications — <c>platform_api_keys</c> and Hangfire are documented and unbuilt on purpose,
/// and a rule that forbade that would force the design documents down to whatever had been built
/// last, which is the opposite of what they are for.
/// </para>
/// <para>
/// They check that a definition <i>exists</i>, not that it is correct. Nothing here can tell
/// whether a documented column list matches the real one — that is a reading task, and Milestone
/// 12.1 did it. What these prevent is the silent case: a table added with no mention at all.
/// </para>
/// </remarks>
public sealed class DocumentationDriftTests
{
    private static readonly string DocsRoot =
        Path.Combine(Directory.GetParent(BackendLayout.Root)!.FullName, "docs");

    [Fact]
    public void EveryTableCreatedByAMigration_IsDocumented()
    {
        // The failure this catches has no symptom. A table lands in a migration, works perfectly,
        // and 06-database simply never mentions it — so the next person to design against the
        // schema reads a document that is missing a relation, and there is nothing to notice.
        var documented = File.ReadAllText(Path.Combine(DocsRoot, "06-database", "database-design.md"));

        var offenders = TablesCreatedByMigrations()
            .Where(table => !documented.Contains($"`{table}`", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheTableRule_FoundTablesToCheck()
    {
        // If the migration parser stops matching — a formatting change in the generated code, a
        // move to raw SQL — this rule would pass over an empty set forever.
        var tables = TablesCreatedByMigrations();

        Assert.NotEmpty(tables);
        Assert.Contains("employees", tables);
    }

    [Fact]
    public void EveryReferencedPackage_IsInTheTechnologyInventory()
    {
        // Central package management means one file declares every version, which makes the
        // inventory checkable rather than aspirational. A package that enters the build without
        // appearing in 04-technology has bypassed the dependency policy that document exists to
        // apply — and the first sign of that is usually a licence question nobody asked.
        var inventory = File.ReadAllText(
            Path.Combine(DocsRoot, "04-technology", "backend-technologies.md"));

        var offenders = ReferencedPackages()
            .Where(package => !inventory.Contains(package, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ThePackageRule_FoundPackagesToCheck()
    {
        var packages = ReferencedPackages();

        Assert.NotEmpty(packages);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", packages);
    }

    [Fact]
    public void NoIdentityTable_ClaimsAForeignKeyAcrossAModuleSchema()
    {
        // ADR-0002 R-6 and CLAUDE.md §9 both forbid this, and 06-database §3.3 asserted one anyway
        // for two phases — `employees.company_id` was listed as "same schema" when it crosses
        // identity into tenancy. The code was always right; the table was wrong.
        //
        // Stated against the migrations rather than the document, because the migrations are what
        // the database ends up with. A cross-schema constraint here would fail to apply at all if
        // the other schema were extracted to a service, which is the outcome R-6 exists to keep
        // possible.
        var crossSchema = new Regex(
            @"ReferencedSchema:\s*""(?<schema>\w+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var offenders = MigrationFiles()
            .SelectMany(path => crossSchema
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups["schema"].Value)
                .Where(static schema => schema != "identity")
                .Select(schema => $"{Path.GetFileName(path)} references schema '{schema}'"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IReadOnlyList<string> TablesCreatedByMigrations()
    {
        // Matches the `name:` argument of CreateTable in the generated migration code.
        var createTable = new Regex(
            @"migrationBuilder\s*\.\s*CreateTable\s*\(\s*name:\s*""(?<table>\w+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return [.. MigrationFiles()
            .SelectMany(path => createTable.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["table"].Value))
            .Where(static table => table != "__EFMigrationsHistory")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Migration files, excluding the designer and snapshot files.
    /// </summary>
    /// <remarks>
    /// The snapshot restates the whole model on every migration, so including it would report
    /// every table against whichever migration was generated last rather than the one that
    /// created it.
    /// </remarks>
    private static IReadOnlyList<string> MigrationFiles() =>
        [.. Directory
            .EnumerateFiles(
                Path.Combine(BackendLayout.Root, "src", "MaintOrbit.Infrastructure", "Persistence", "Migrations"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(static path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> ReferencedPackages()
    {
        var packageVersion = new Regex(
            @"<PackageVersion\s+Include=""(?<id>[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var props = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Packages.props"));

        return [.. packageVersion.Matches(props)
            .Select(match => match.Groups["id"].Value)
            .Order(StringComparer.Ordinal)];
    }
}
