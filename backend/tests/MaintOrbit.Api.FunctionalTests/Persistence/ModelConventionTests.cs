using MaintOrbit.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers the model-wide conventions against a probe model.
/// </summary>
/// <remarks>
/// The real model has no entity types — D-1 blocks schema design — so the conventions are
/// exercised against a probe defined here. Testing them now rather than when the first entity
/// lands is the point: a naming convention that is wrong is discovered by reading the schema,
/// which is exactly what nobody does until something else has gone wrong.
/// </remarks>
public sealed class ModelConventionTests
{
    private const string TestConnectionString =
        "Host=localhost;Database=maintorbit_test;Username=maintorbit";

    [Theory]
    [InlineData("ProviderConnections", "provider_connections")]
    [InlineData("CreatedAtUtc", "created_at_utc")]
    [InlineData("CompanyId", "company_id")]
    [InlineData("UsageRecords", "usage_records")]
    [InlineData("PK_Employees", "pk_employees")]
    [InlineData("Id", "id")]
    [InlineData("already_snake", "already_snake")]
    public void ToSnakeCase_MatchesTheDocumentedForm(string input, string expected)
    {
        Assert.Equal(expected, NamingConventions.ToSnakeCase(input));
    }

    [Theory]
    [InlineData("APIKeyHash", "api_key_hash")]
    [InlineData("DEKVersion", "dek_version")]
    [InlineData("HTTPStatusCode", "http_status_code")]
    public void ToSnakeCase_KeepsAcronymsIntact(string input, string expected)
    {
        // §1.5 forbids abbreviating platform terms, so acronyms appear in identifiers and must
        // not be shredded into single letters.
        Assert.Equal(expected, NamingConventions.ToSnakeCase(input));
    }

    [Fact]
    public void TablesAndColumns_AreSnakeCase()
    {
        using var context = new ProbeDbContext();

        var entity = Assert.Single(context.Model.GetEntityTypes());

        Assert.Equal("probe_records", entity.GetTableName());
        Assert.Equal("tenancy", entity.GetSchema());

        var columns = entity.GetProperties().Select(static p => p.GetColumnName()).ToList();
        Assert.Contains("id", columns);
        Assert.Contains("company_id", columns);
        Assert.Contains("created_at_utc", columns);
    }

    [Fact]
    public void Indexes_UseTheDocumentedPrefixAndColumns()
    {
        // §1.5: ix_<table>_<columns>, and ux_ when unique. Naming an index after what it covers
        // is what makes a duplicate obvious in a schema listing.
        using var context = new ProbeDbContext();
        var entity = Assert.Single(context.Model.GetEntityTypes());

        var names = entity.GetIndexes().Select(static i => i.GetDatabaseName()).ToList();

        Assert.Contains("ix_probe_records_company_id_created_at_utc", names);
        Assert.Contains("ux_probe_records_company_id", names);
    }

    [Fact]
    public void EntityWithoutASchema_IsRejected()
    {
        // DB-P2. EF's default would silently place the table in `public`; the module boundary
        // it belongs to is what makes later extraction possible, so the omission must fail.
        using var context = new SchemalessProbeDbContext();

        var failure = Assert.Throws<InvalidOperationException>(() => context.Model);

        Assert.Contains("DB-P2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ProbeRecord), failure.Message, StringComparison.Ordinal);
    }

    private sealed class ProbeRecord
    {
        public Guid Id { get; init; }
        public Guid CompanyId { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
    }

    private class ProbeDbContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql(TestConnectionString);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeRecord>(entity =>
            {
                ConfigureProbe(entity);
                entity.HasIndex(r => new { r.CompanyId, r.CreatedAtUtc });
                entity.HasIndex(r => r.CompanyId).IsUnique();
            });

            modelBuilder.RequireExplicitSchema();
            modelBuilder.ApplySnakeCaseNames();
        }

        protected virtual void ConfigureProbe(
            Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ProbeRecord> entity) =>
            entity.ToTable("ProbeRecords", "tenancy");
    }

    private sealed class SchemalessProbeDbContext : ProbeDbContext
    {
        protected override void ConfigureProbe(
            Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ProbeRecord> entity) =>
            entity.ToTable("ProbeRecords");
    }
}
