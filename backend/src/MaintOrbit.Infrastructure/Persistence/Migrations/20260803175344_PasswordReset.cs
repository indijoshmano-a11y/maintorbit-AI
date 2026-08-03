using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PasswordReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_from_ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invalidated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_tokens", x => x.id);
                    table.CheckConstraint("ck_password_reset_tokens_expiry", "expires_at_utc > requested_at_utc");
                    table.ForeignKey(
                        name: "fk_password_reset_tokens_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_employee_id_outstanding",
                schema: "identity",
                table: "password_reset_tokens",
                column: "employee_id",
                filter: "consumed_at_utc IS NULL AND invalidated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_password_reset_tokens_token_hash",
                schema: "identity",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            // ---- Row-level security ------------------------------------------------------
            //
            // Tenant-scoped, so it carries its policy in the same migration that creates it —
            // CLAUDE.md §9: "a table without one is a leak". This one is C4 and unauthenticated
            // paths write to it, which makes the policy the only thing standing between a reset
            // request and another Company's rows.
            //
            // FORCE is what makes it real. PostgreSQL exempts a table's owner from its own
            // policies by default and migrations run as owner, so without FORCE the policy exists,
            // reads correctly, and filters nothing for the account most likely to be used by a
            // script or an operator.
            migrationBuilder.Sql(
                "ALTER TABLE identity.password_reset_tokens ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.password_reset_tokens FORCE ROW LEVEL SECURITY;");

            // USING alone would filter reads while leaving a caller able to insert a row against
            // another Company — which returns as a successful insert.
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_password_reset_tokens ON identity.password_reset_tokens
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_password_reset_tokens ON identity.password_reset_tokens;");

            migrationBuilder.DropTable(
                name: "password_reset_tokens",
                schema: "identity");
        }
    }
}
