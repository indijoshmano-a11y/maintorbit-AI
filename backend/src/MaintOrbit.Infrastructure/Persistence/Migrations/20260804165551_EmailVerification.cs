using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_verification_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invalidated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verification_tokens", x => x.id);
                    table.CheckConstraint("ck_email_verification_tokens_expiry", "expires_at_utc > issued_at_utc");
                    table.ForeignKey(
                        name: "fk_email_verification_tokens_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_tokens_employee_id_outstanding",
                schema: "identity",
                table: "email_verification_tokens",
                column: "employee_id",
                filter: "consumed_at_utc IS NULL AND invalidated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_email_verification_tokens_token_hash",
                schema: "identity",
                table: "email_verification_tokens",
                column: "token_hash",
                unique: true);

            // ---- Row-level security ------------------------------------------------------
            //
            // Tenant-scoped and C4, like every other token table here. CLAUDE.md §9: "a table
            // without one is a leak" — and the leak would be one Company's verification links,
            // each of which proves control of an address to whoever holds it.
            //
            // FORCE, because PostgreSQL exempts a table's owner from its own policies by default
            // and migrations run as owner.
            migrationBuilder.Sql(
                "ALTER TABLE identity.email_verification_tokens ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.email_verification_tokens FORCE ROW LEVEL SECURITY;");

            // USING alone would filter reads while leaving a caller able to insert a row against
            // another Company — which returns as a successful insert.
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_email_verification_tokens ON identity.email_verification_tokens
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_email_verification_tokens " +
                "ON identity.email_verification_tokens;");

            migrationBuilder.DropTable(
                name: "email_verification_tokens",
                schema: "identity");
        }
    }
}
