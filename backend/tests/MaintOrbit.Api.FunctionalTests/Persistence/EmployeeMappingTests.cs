using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers how <see cref="Employee"/> maps to <c>identity.employees</c>.
/// </summary>
/// <remarks>
/// Asserted against the built model rather than against generated SQL, so the rules hold without
/// a database. Every name here is stated in database-design §1.5, §1.7, or §4.2 — a mapping that
/// drifts from those produces a schema the documentation no longer describes, which is only
/// discovered by reading both.
/// </remarks>
public sealed class EmployeeMappingTests
{
    private static IEntityType EmployeeType()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        // The design-time model, not context.Model. The runtime model is read-optimized and
        // drops metadata that only migrations need — check constraints among it.
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Employee))!;
    }

    [Fact]
    public void Employee_IsMappedIntoTheIdentitySchema()
    {
        // DB-P2: one schema per module, named for the module. The schema convention would have
        // thrown during model building if this were absent.
        var entity = EmployeeType();

        Assert.Equal("employees", entity.GetTableName());
        Assert.Equal("identity", entity.GetSchema());
    }

    [Fact]
    public void TableName_IsPluralSnakeCase()
    {
        // The configuration writes "Employees"; the convention converts it. Asserting the result
        // is what keeps the convention load-bearing rather than incidental.
        Assert.Equal("employees", EmployeeType().GetTableName());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("company_id")]
    [InlineData("email")]
    [InlineData("email_verified_at_utc")]
    [InlineData("status")]
    [InlineData("primary_team_id")]
    [InlineData("deleted_at_utc")]
    [InlineData("deleted_by_employee_id")]
    [InlineData("pseudonymized_at_utc")]
    [InlineData("created_at_utc")]
    [InlineData("created_by_employee_id")]
    [InlineData("updated_at_utc")]
    [InlineData("updated_by_employee_id")]
    [InlineData("row_version")]
    public void DocumentedColumn_ExistsInSnakeCase(string column)
    {
        var columns = EmployeeType().GetProperties()
            .Select(static property => property.GetColumnName())
            .ToList();

        Assert.Contains(column, columns);
    }

    [Fact]
    public void NoUndocumentedColumn_WasIntroduced()
    {
        // "Do not invent fields" is only checkable in this direction. The set below is exactly
        // §4.2's key columns plus §1.7's standard audit fields plus §1.8's soft-delete columns.
        string[] expected =
        [
            "company_id", "created_at_utc", "created_by_employee_id", "deleted_at_utc",
            "deleted_by_employee_id", "email", "email_verified_at_utc", "id",
            "primary_team_id", "pseudonymized_at_utc", "row_version", "status",
            "updated_at_utc", "updated_by_employee_id"
        ];

        var actual = EmployeeType().GetProperties()
            .Select(static property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrimaryKey_IsId()
    {
        var key = EmployeeType().FindPrimaryKey()!;

        Assert.Equal("id", Assert.Single(key.Properties).GetColumnName());
    }

    [Fact]
    public void EmailIsUniquePerCompany_ExceptForDeletedRows()
    {
        // §1.8 and §9.x: the index is partial so removing an Employee does not permanently
        // reserve their address against their own Company.
        var index = EmployeeType().GetIndexes()
            .Single(static i => i.IsUnique);

        Assert.Equal("ux_employees_company_id_email", index.GetDatabaseName());
        Assert.Equal("deleted_at_utc IS NULL", index.GetFilter());
    }

    [Fact]
    public void StatusIsIndexedPerCompany()
    {
        var names = EmployeeType().GetIndexes()
            .Select(static index => index.GetDatabaseName())
            .ToList();

        Assert.Contains("ix_employees_company_id_status", names);
    }

    [Fact]
    public void EveryIndex_LeadsWithTheTenantDiscriminator()
    {
        // Every query against this table is tenant-scoped, because row-level security appends
        // the Company predicate to all of them. An index that does not lead with company_id
        // cannot serve those queries.
        var leadingColumns = EmployeeType().GetIndexes()
            .Select(static index => index.Properties[0].GetColumnName())
            .Distinct()
            .ToList();

        Assert.Equal(["company_id"], leadingColumns);
    }

    [Fact]
    public void Status_IsStoredAsReadableText_AndConstrained()
    {
        var entity = EmployeeType();
        var status = entity.GetProperty(nameof(Employee.Status));

        Assert.Equal(typeof(string), status.GetProviderClrType());

        // The check constraint is what closes the set for writers that are not this application.
        var constraint = Assert.Single(entity.GetCheckConstraints());
        Assert.Equal("ck_employees_status", constraint.Name);
        Assert.Contains("Invited", constraint.Sql, StringComparison.Ordinal);
        Assert.Contains("Removed", constraint.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Email_IsBoundedAtTheStandardLength()
    {
        var email = EmployeeType().GetProperty(nameof(Employee.Email));

        Assert.Equal(254, email.GetMaxLength());
        Assert.False(email.IsNullable);
    }

    [Fact]
    public void TenantDiscriminatorAndTimestamps_AreRequired()
    {
        var entity = EmployeeType();

        Assert.False(entity.GetProperty(nameof(Employee.CompanyId)).IsNullable);
        Assert.False(entity.GetProperty(nameof(Employee.CreatedAtUtc)).IsNullable);
        Assert.False(entity.GetProperty(nameof(Employee.UpdatedAtUtc)).IsNullable);
    }

    [Fact]
    public void SoftDeleteAndErasureColumns_AreNullable()
    {
        // A row that has never been deleted or pseudonymized must be representable, and the
        // partial unique index depends on deleted_at_utc being null for live rows.
        var entity = EmployeeType();

        Assert.True(entity.GetProperty(nameof(Employee.DeletedAtUtc)).IsNullable);
        Assert.True(entity.GetProperty(nameof(Employee.PseudonymizedAtUtc)).IsNullable);
        Assert.True(entity.GetProperty(nameof(Employee.EmailVerifiedAtUtc)).IsNullable);
    }

    [Fact]
    public void Identifier_IsNeverGeneratedByTheDatabase()
    {
        // TD-5 is open: PostgreSQL 18 has a native UUIDv7 generator and 17 does not. The
        // application generates, which is correct under either outcome.
        var id = EmployeeType().GetProperty(nameof(Employee.Id));

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void RowVersion_IsAConcurrencyToken()
    {
        Assert.True(EmployeeType().GetProperty(nameof(Employee.RowVersion)).IsConcurrencyToken);
    }

    [Fact]
    public void Timestamps_MapToTimestampWithTimeZone()
    {
        // §1.7: timestamptz always, never timestamp. DateTimeOffset is what Npgsql maps to it;
        // a DateTime would map to `timestamp without time zone` and silently drop the offset.
        var entity = EmployeeType();

        foreach (var name in new[] { nameof(Employee.CreatedAtUtc), nameof(Employee.UpdatedAtUtc) })
        {
            Assert.Equal(typeof(DateTimeOffset), entity.GetProperty(name).ClrType);
        }
    }

    [Fact]
    public void NoForeignKey_CrossesAModuleSchema()
    {
        // DB-P2. company_id and primary_team_id both point into the tenancy schema and are
        // carried as identifiers only. database-design §3.3 lists employees.company_id as
        // FK-enforced "same schema", which it is not — companies live in tenancy. The frozen
        // rule wins over the table row.
        Assert.Empty(EmployeeType().GetForeignKeys());
    }
}
