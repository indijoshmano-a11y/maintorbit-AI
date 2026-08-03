using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers the credential mapping and the migration that creates it.
/// </summary>
public sealed class EmployeeCredentialMappingTests
{
    private static IEntityType CredentialType()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(EmployeeCredential))!;
    }

    private static string Script()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        return context.GetService<IMigrator>().GenerateScript();
    }

    [Fact]
    public void Credential_IsMappedIntoTheIdentitySchema()
    {
        var entity = CredentialType();

        Assert.Equal("employee_credentials", entity.GetTableName());
        Assert.Equal("identity", entity.GetSchema());
    }

    [Fact]
    public void NoUndocumentedColumn_WasIntroduced()
    {
        string[] expected =
        [
            "algorithm", "company_id", "created_at_utc", "created_by_employee_id", "employee_id",
            "failed_login_count", "hash_parameters", "id", "lockout_until_utc", "password_changed_at_utc",
            "password_hash", "password_version", "require_password_change", "row_version",
            "updated_at_utc", "updated_by_employee_id"
        ];

        var actual = CredentialType().GetProperties()
            .Select(static property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PasswordExpiry_IsAbsent()
    {
        // Deliberate. compliance §14 lists no expiry and prefers breach-corpus checking to
        // rotation: a long, unique, unbreached password is stronger than one rotated on a
        // schedule. Adding the column would invite the policy.
        var columns = CredentialType().GetProperties()
            .Select(static property => property.GetColumnName())
            .ToList();

        Assert.DoesNotContain("password_expires_at_utc", columns);
    }

    [Fact]
    public void TenantDiscriminator_IsCarriedOnTheTableItself()
    {
        // DB-P1 requires company_id on every tenant-scoped relation. Reaching it through a join
        // to employees would make the policy a per-row subquery on the most sensitive table here.
        Assert.False(CredentialType().GetProperty(nameof(EmployeeCredential.CompanyId)).IsNullable);
    }

    [Fact]
    public void OneCredentialPerEmployee_IsEnforced()
    {
        // 1 : 0..1. Unique on employee_id alone, not (company_id, employee_id): an EmployeeId is
        // globally unique, so a Company-scoped constraint would permit a second credential under
        // a different company_id — a row RLS would then hide from the Company that owns it.
        var index = Assert.Single(CredentialType().GetIndexes());

        Assert.True(index.IsUnique);
        Assert.Equal("ux_employee_credentials_employee_id", index.GetDatabaseName());
        Assert.Equal("employee_id", Assert.Single(index.Properties).GetColumnName());
    }

    [Fact]
    public void ForeignKeyToEmployees_StaysWithinTheIdentitySchema()
    {
        // DB-P2 permits a foreign key inside one schema and forbids one across schemas. §3.3
        // shows the sibling case, sessions.employee_id -> employees.id, as FK-enforced.
        var foreignKey = Assert.Single(CredentialType().GetForeignKeys());

        Assert.Equal("identity", foreignKey.PrincipalEntityType.GetSchema());
        Assert.Equal("employees", foreignKey.PrincipalEntityType.GetTableName());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void HashAndAlgorithm_AreBoundedAndRequired()
    {
        var entity = CredentialType();

        Assert.Equal(256, entity.GetProperty(nameof(EmployeeCredential.PasswordHash)).GetMaxLength());
        Assert.False(entity.GetProperty(nameof(EmployeeCredential.PasswordHash)).IsNullable);
        Assert.Equal(typeof(string), entity.GetProperty(nameof(EmployeeCredential.Algorithm)).GetProviderClrType());
    }

    [Fact]
    public void LockoutEndsAreNullable_AndCountersAreNot()
    {
        var entity = CredentialType();

        Assert.True(entity.GetProperty(nameof(EmployeeCredential.LockoutUntilUtc)).IsNullable);
        Assert.False(entity.GetProperty(nameof(EmployeeCredential.FailedLoginCount)).IsNullable);
    }

    [Theory]
    [InlineData("ck_employee_credentials_algorithm")]
    [InlineData("ck_employee_credentials_failed_login_count")]
    [InlineData("ck_employee_credentials_password_version")]
    public void CheckConstraint_IsDefined(string name)
    {
        Assert.Contains(CredentialType().GetCheckConstraints(), c => c.Name == name);
    }

    // ---- Migration ----------------------------------------------------------------------------

    [Fact]
    public void MigrationsIncludeTheCredentialTable()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        var migrations = context.Database.GetMigrations().ToList();

        Assert.Equal(5, migrations.Count);
        Assert.Contains(migrations, m => m.EndsWith("EmployeeCredentials", StringComparison.Ordinal));
    }

    [Fact]
    public void Migrations_CreateNoIdentityTableBeyondThoseBuiltSoFar()
    {
        // The script is cumulative, so this widens as milestones land. What it still excludes is
        // what has not been built.
        var sql = Script();

        foreach (var absent in new[]
                 {
                     "mfa_enrollments", "mfa_recovery_codes", "federated_identities",
                     "platform_api_keys", "companies"
                 })
        {
            Assert.DoesNotContain(absent, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Migration_EnablesAndForcesRowLevelSecurity()
    {
        var sql = Script();

        Assert.Contains(
            "ALTER TABLE identity.employee_credentials ENABLE ROW LEVEL SECURITY",
            sql, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE identity.employee_credentials FORCE ROW LEVEL SECURITY",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_CreatesThePolicyUnderTheDocumentedName()
    {
        Assert.Contains(
            $"CREATE POLICY {TenantSession.PolicyName("employee_credentials")} ON identity.employee_credentials",
            Script(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialPolicy_UsesTheSamePredicateAsTheInterceptor()
    {
        // Drift between the two would make every credential query return zero rows — safe, and
        // completely silent.
        var policy = Script();
        var occurrences = policy.Split(TenantSession.CurrentCompanyExpression).Length - 1;

        // Six tenant-scoped tables, each with USING and WITH CHECK: employees,
        // employee_credentials, sessions, refresh_tokens, employee_roles, and
        // password_reset_tokens. Every one of them is a policy that would silently return zero
        // rows if its predicate drifted from what sets the variable.
        Assert.Equal(12, occurrences);
    }
}
