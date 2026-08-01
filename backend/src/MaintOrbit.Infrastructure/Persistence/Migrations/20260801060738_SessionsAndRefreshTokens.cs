using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionsAndRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    client_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    coarse_location = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_active_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_absolute_expiry", "absolute_expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_sessions_client_type", "client_type IN ('Unknown', 'WebConsole', 'VsCodeExtension', 'ServerApplication')");
                    table.CheckConstraint("ck_sessions_revocation", "(revoked_at_utc IS NULL) = (revocation_reason IS NULL)");
                    table.ForeignKey(
                        name: "fk_sessions_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.CheckConstraint("ck_refresh_tokens_expiry", "expires_at_utc > issued_at_utc");
                    table.ForeignKey(
                        name: "fk_refresh_tokens_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "identity",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_company_id",
                schema: "identity",
                table: "sessions",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_employee_id_active",
                schema: "identity",
                table: "sessions",
                column: "employee_id",
                filter: "revoked_at_utc IS NULL");

            // ---- Row-level security ------------------------------------------------------
            //
            // Both tables in the same migration that creates them (DB-P9). refresh_tokens is C4,
            // so the window a follow-up migration would leave open is a window on bearer
            // credentials.
            //
            // FORCE applies the policy to the table owner as well. Migrations run as owner, and
            // PostgreSQL exempts an owner from its own policies by default — without it the policy
            // exists, reads correctly, and filters nothing for that account.
            foreach (var table in new[] { "sessions", "refresh_tokens" })
            {
                migrationBuilder.Sql($"ALTER TABLE identity.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE identity.{table} FORCE ROW LEVEL SECURITY;");

                // Predicate written out rather than composed from TenantSession: an applied
                // migration records what was applied. A test asserts the two still agree.
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
            foreach (var table in new[] { "refresh_tokens", "sessions" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS rls_{table} ON identity.{table};");
            }

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "identity");
        }
    }
}
