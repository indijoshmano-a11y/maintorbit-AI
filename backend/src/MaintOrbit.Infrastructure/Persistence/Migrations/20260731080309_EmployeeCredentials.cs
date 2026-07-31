using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_credentials",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    password_version = table.Column<int>(type: "integer", nullable: false),
                    hash_parameters = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    password_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    require_password_change = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee_credentials", x => x.id);
                    table.CheckConstraint("ck_employee_credentials_algorithm", "algorithm IN ('Argon2id')");
                    table.CheckConstraint("ck_employee_credentials_failed_login_count", "failed_login_count >= 0");
                    table.CheckConstraint("ck_employee_credentials_password_version", "password_version >= 1");
                    table.ForeignKey(
                        name: "fk_employee_credentials_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_employee_credentials_employee_id",
                schema: "identity",
                table: "employee_credentials",
                column: "employee_id",
                unique: true);

            // ---- Row-level security ------------------------------------------------------
            //
            // Same migration as the table (DB-P9). This one carries C4 material, so the window a
            // follow-up migration would leave open is a window on password hashes.
            //
            // FORCE is what makes the policy apply to the table owner. Migrations run as owner,
            // and PostgreSQL exempts an owner from its own policies by default.
            migrationBuilder.Sql("ALTER TABLE identity.employee_credentials ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE identity.employee_credentials FORCE ROW LEVEL SECURITY;");

            // Predicate written out rather than composed from TenantSession: an applied migration
            // records what was applied. A test asserts the two still agree.
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_employee_credentials ON identity.employee_credentials
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_employee_credentials ON identity.employee_credentials;");

            migrationBuilder.DropTable(
                name: "employee_credentials",
                schema: "identity");
        }
    }
}
