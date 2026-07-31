using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "employees",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    email_verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    primary_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pseudonymized_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employees", x => x.id);
                    table.CheckConstraint("ck_employees_status", "status IN ('Invited', 'Active', 'Suspended', 'Removed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_employees_company_id_status",
                schema: "identity",
                table: "employees",
                columns: new[] { "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_employees_company_id_email",
                schema: "identity",
                table: "employees",
                columns: new[] { "company_id", "email" },
                unique: true,
                filter: "deleted_at_utc IS NULL");

            // ---- Row-level security ------------------------------------------------------
            //
            // In the same migration as the table (DB-P9). A tenant-scoped table without a policy
            // is a leak, and a follow-up migration leaves a window in which it is one.
            //
            // ENABLE makes the policy apply. FORCE makes it apply to the table owner as well,
            // and that second statement is the one that matters: PostgreSQL exempts the owner
            // from its own policies by default, and migrations run as owner. Without FORCE the
            // policy exists, reads correctly in \d+, and filters nothing for the account most
            // likely to be used by a script or an operator.
            //
            // The predicate is written out rather than composed from TenantSession, because an
            // applied migration is a record of what was applied. If it read a constant, editing
            // that constant would silently change history. A test asserts the two agree.
            migrationBuilder.Sql("ALTER TABLE identity.employees ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE identity.employees FORCE ROW LEVEL SECURITY;");

            // USING filters what is visible to SELECT, UPDATE, and DELETE.
            // WITH CHECK constrains what INSERT and UPDATE may write — without it a caller could
            // create a row belonging to another Company, which reads as a successful insert.
            //
            // An unset, cleared, or malformed session variable yields NULL, and `company_id =
            // NULL` is NULL rather than true, so the policy matches nothing: zero rows, never
            // unfiltered rows (§5.2).
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_employees ON identity.employees
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped explicitly before the table. Dropping the table would remove the policy
            // with it, but stating it keeps the reversal readable as the inverse of Up.
            migrationBuilder.Sql("DROP POLICY IF EXISTS rls_employees ON identity.employees;");

            migrationBuilder.DropTable(
                name: "employees",
                schema: "identity");
        }
    }
}
