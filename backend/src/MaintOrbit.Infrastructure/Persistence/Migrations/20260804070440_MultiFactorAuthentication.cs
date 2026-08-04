using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiFactorAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mfa_enrollments",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret_iv = table.Column<byte[]>(type: "bytea", maxLength: 12, nullable: false),
                    secret_auth_tag = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: false),
                    dek_version = table.Column<int>(type: "integer", nullable: false),
                    algorithm_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_accepted_time_step = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_enrollments", x => x.id);
                    table.CheckConstraint("ck_mfa_enrollments_confirmation", "(status = 'Pending') = (confirmed_at_utc IS NULL)");
                    table.CheckConstraint("ck_mfa_enrollments_status", "status IN ('Pending', 'Confirmed', 'Disabled')");
                    table.ForeignKey(
                        name: "fk_mfa_enrollments_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_recovery_codes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_recovery_codes_mfa_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalSchema: "identity",
                        principalTable: "mfa_enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_mfa_enrollments_employee_id_active",
                schema: "identity",
                table: "mfa_enrollments",
                column: "employee_id",
                unique: true,
                filter: "disabled_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_mfa_recovery_codes_enrollment_id_code_hash",
                schema: "identity",
                table: "mfa_recovery_codes",
                columns: new[] { "enrollment_id", "code_hash" },
                unique: true);

            // ---- Row-level security ------------------------------------------------------
            //
            // Both tables, because both are tenant-scoped and both are C4. CLAUDE.md §9: "a table
            // without one is a leak" — and here the leak would be one Company's TOTP secrets and
            // recovery codes visible to another.
            //
            // FORCE is the statement that decides whether any of it works. PostgreSQL exempts a
            // table's owner from its own policies by default and migrations run as owner, so
            // without it the policy exists, reads correctly, and filters nothing for the account
            // most likely to be used by a script or an operator.
            foreach (var table in new[] { "mfa_enrollments", "mfa_recovery_codes" })
            {
                migrationBuilder.Sql($"ALTER TABLE identity.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE identity.{table} FORCE ROW LEVEL SECURITY;");

                // USING alone would filter reads while leaving a caller able to insert a row
                // against another Company — which returns as a successful insert.
                migrationBuilder.Sql(
                    $"""
                     CREATE POLICY rls_{table} ON identity.{table}
                         USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                         WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "mfa_recovery_codes", "mfa_enrollments" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS rls_{table} ON identity.{table};");
            }

            migrationBuilder.DropTable(
                name: "mfa_recovery_codes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "mfa_enrollments",
                schema: "identity");
        }
    }
}
