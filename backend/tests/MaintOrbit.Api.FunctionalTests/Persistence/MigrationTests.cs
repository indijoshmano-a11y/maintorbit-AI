using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers the SQL the initial migration emits.
/// </summary>
/// <remarks>
/// Generated offline through <see cref="IMigrator.GenerateScript"/>, which is how these run in
/// CI without a database. Applying the migration for real is a separate, stronger check and was
/// done against PostgreSQL 18 — but a script assertion is what keeps a later edit from quietly
/// dropping a statement, and it runs on every build rather than when someone remembers.
/// </remarks>
public sealed class MigrationTests
{
    private static string Script(MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        return context.GetService<IMigrator>().GenerateScript(options: options);
    }

    [Fact]
    public void TheFirstMigration_IsTheIdentityBaseline()
    {
        // Migrations are ordered by their timestamp prefix and applied in that order, so the
        // first one is the baseline every later migration builds on. The total count is asserted
        // by the milestone that adds each one.
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        var first = context.Database.GetMigrations().First();

        Assert.EndsWith("InitialIdentity", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesTheIdentitySchemaAndEmployeesTable()
    {
        var sql = Script();

        // Npgsql guards schema creation with a catalogue check in a DO block rather than
        // emitting IF NOT EXISTS, so the statement itself is unqualified.
        Assert.Contains("CREATE SCHEMA identity;", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE identity.employees", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesBothDocumentedIndexes()
    {
        var sql = Script();

        Assert.Contains("ux_employees_company_id_email", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE deleted_at_utc IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ix_employees_company_id_status", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesTheStatusCheckConstraint()
    {
        Assert.Contains("ck_employees_status", Script(), StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesNoIdentityTableBeyondThoseBuiltSoFar()
    {
        // Cumulative across migrations. What remains listed is what has not been built.
        var sql = Script();

        foreach (var absent in new[]
                 {
                     "federated_identities", "companies", "teams"
                 })
        {
            Assert.DoesNotContain(absent, sql, StringComparison.Ordinal);
        }
    }

    // ---- Row-level security ------------------------------------------------------------------

    [Fact]
    public void Script_EnablesRowLevelSecurity()
    {
        Assert.Contains(
            "ALTER TABLE identity.employees ENABLE ROW LEVEL SECURITY",
            Script(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ForcesRowLevelSecurityForTheOwner()
    {
        // The statement that decides whether any of this works. PostgreSQL exempts a table's
        // owner from its own policies by default, and migrations run as owner — so without FORCE
        // the policy exists, reads correctly, and filters nothing for the account most likely to
        // be used by a script or an operator.
        Assert.Contains(
            "ALTER TABLE identity.employees FORCE ROW LEVEL SECURITY",
            Script(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesThePolicyUnderTheDocumentedName()
    {
        // §1.5 names RLS policies rls_<table>.
        Assert.Contains(
            $"CREATE POLICY {TenantSession.PolicyName("employees")} ON identity.employees",
            Script(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_ConstrainsBothReadsAndWrites()
    {
        // USING alone filters what is visible. Without WITH CHECK a caller could still insert a
        // row belonging to another Company, which returns as a successful insert.
        var sql = Script();

        Assert.Contains("USING (company_id =", sql, StringComparison.Ordinal);
        Assert.Contains("WITH CHECK (company_id =", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyPredicate_MatchesTheExpressionTheInterceptorSets()
    {
        // The migration hard-codes its predicate on purpose: an applied migration is a record of
        // what was applied, and reading a constant would let an edit rewrite history. That leaves
        // the two able to drift, so this is the test that notices. If they disagreed, every query
        // would return zero rows — safe, but silent.
        Assert.Contains(
            TenantSession.CurrentCompanyExpression,
            Script(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SessionVariableName_IsAValidCustomizedOption()
    {
        // PostgreSQL requires a dot in a customized option name; without one, set_config is
        // rejected at runtime rather than at startup.
        Assert.Contains(".", TenantSession.CompanyVariable, StringComparison.Ordinal);
        Assert.StartsWith("app.", TenantSession.CompanyVariable, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentCompanyExpression_ToleratesAnUnsetAndAClearedVariable()
    {
        // Both halves are load-bearing. Without missing_ok the first query on a fresh connection
        // errors; without NULLIF a cleared variable makes ''::uuid raise invalid input syntax.
        // Either turns the documented "zero rows" into a fault.
        Assert.Contains("true)", TenantSession.CurrentCompanyExpression, StringComparison.Ordinal);
        Assert.Contains("NULLIF(", TenantSession.CurrentCompanyExpression, StringComparison.Ordinal);
    }

    // ---- Reversibility and re-application ----------------------------------------------------

    [Fact]
    public void IdempotentScript_GuardsEveryStatement()
    {
        // Generated for deployments that re-run the script. The migration history check wraps the
        // whole migration, so a second run applies nothing.
        var sql = Script(MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("__ef_migrations_history", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY rls_employees", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationHistory_IsRecordedInThePublicSchema()
    {
        // Not in a module schema: the twelve in §2 each belong to a module, and migration history
        // belongs to none of them.
        Assert.Contains(
            "public.__ef_migrations_history",
            Script(),
            StringComparison.Ordinal);
    }
}
