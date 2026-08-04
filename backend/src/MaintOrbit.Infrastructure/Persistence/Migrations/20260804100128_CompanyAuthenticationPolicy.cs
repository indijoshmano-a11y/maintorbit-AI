using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyAuthenticationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_authentication_policies",
                schema: "identity",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_password_length = table.Column<int>(type: "integer", nullable: false),
                    require_breach_check = table.Column<bool>(type: "boolean", nullable: false),
                    idle_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    absolute_lifetime_minutes = table.Column<int>(type: "integer", nullable: false),
                    mfa_required = table.Column<bool>(type: "boolean", nullable: false),
                    maximum_failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    lockout_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_authentication_policies", x => x.company_id);
                    table.CheckConstraint("ck_company_authentication_policies_absolute_lifetime", "absolute_lifetime_minutes BETWEEN 15 AND 43200");
                    table.CheckConstraint("ck_company_authentication_policies_failed_attempts", "maximum_failed_attempts BETWEEN 3 AND 20");
                    table.CheckConstraint("ck_company_authentication_policies_idle_timeout", "idle_timeout_minutes BETWEEN 5 AND 43200");
                    table.CheckConstraint("ck_company_authentication_policies_lifetime_order", "absolute_lifetime_minutes >= idle_timeout_minutes");
                    table.CheckConstraint("ck_company_authentication_policies_lockout_minutes", "lockout_minutes BETWEEN 1 AND 1440");
                    table.CheckConstraint("ck_company_authentication_policies_password_length", "minimum_password_length BETWEEN 12 AND 128");
                });

            // ---- Row-level security ------------------------------------------------------
            //
            // Tenant-scoped like every other table that carries a Company, and this one decides
            // how long sessions live and how short a password may be. A policy visible across
            // Companies would let one tenant read another's security posture; a writable one would
            // let them set it.
            //
            // company_id is the primary key here rather than a separate column beside a surrogate
            // one, so the policy predicate is the same expression every other table uses.
            //
            // FORCE, because PostgreSQL exempts a table's owner from its own policies by default
            // and migrations run as owner.
            migrationBuilder.Sql(
                "ALTER TABLE identity.company_authentication_policies ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.company_authentication_policies FORCE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                """
                CREATE POLICY rls_company_authentication_policies ON identity.company_authentication_policies
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_company_authentication_policies " +
                "ON identity.company_authentication_policies;");

            migrationBuilder.DropTable(
                name: "company_authentication_policies",
                schema: "identity");
        }
    }
}
