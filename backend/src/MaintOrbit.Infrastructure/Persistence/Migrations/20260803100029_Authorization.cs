using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Authorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "identity",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "role_definitions",
                schema: "identity",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_definitions", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "employee_roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee_roles", x => x.id);
                    table.CheckConstraint("ck_employee_roles_scope_target", "(scope_type = 'Team') = (scope_id IS NOT NULL)");
                    table.CheckConstraint("ck_employee_roles_scope_type", "scope_type IN ('Company', 'Team', 'Self')");
                    table.ForeignKey(
                        name: "fk_employee_roles_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "identity",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_roles_role_definitions_role_code",
                        column: x => x.role_code,
                        principalSchema: "identity",
                        principalTable: "role_definitions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "identity",
                columns: table => new
                {
                    role_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    permission_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_code, x.permission_code });
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions_permission_code",
                        column: x => x.permission_code,
                        principalSchema: "identity",
                        principalTable: "permissions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_permissions_role_definitions_role_code",
                        column: x => x.role_code,
                        principalSchema: "identity",
                        principalTable: "role_definitions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employee_roles_company_id",
                schema: "identity",
                table: "employee_roles",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_roles_employee_id",
                schema: "identity",
                table: "employee_roles",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_roles_role_code",
                schema: "identity",
                table: "employee_roles",
                column: "role_code");

            migrationBuilder.CreateIndex(
                name: "ux_employee_roles_employee_id_role_code_scope",
                schema: "identity",
                table: "employee_roles",
                columns: new[] { "employee_id", "role_code", "scope_type", "scope_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_code",
                schema: "identity",
                table: "role_permissions",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_role_code",
                schema: "identity",
                table: "role_permissions",
                column: "role_code");

            // ---- Row-level security ------------------------------------------------------
            //
            // Only employee_roles. It is the sole tenant-scoped table of the four: who holds which
            // role varies per Company, while the permission catalogue, the role definitions, and
            // their grants are platform-wide reference data — identical for every Company, and
            // granting nothing to anybody on their own.
            //
            // That is the one deliberate exception to DB-P1 in this schema, and it is recorded
            // rather than assumed: a catalogue row names a capability; only an assignment confers
            // it.
            migrationBuilder.Sql("ALTER TABLE identity.employee_roles ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE identity.employee_roles FORCE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                """
                CREATE POLICY rls_employee_roles ON identity.employee_roles
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_employee_roles ON identity.employee_roles;");

            migrationBuilder.DropTable(
                name: "employee_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_definitions",
                schema: "identity");
        }
    }
}
