using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeRoleUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_employee_roles_employee_id_role_code_scope",
                schema: "identity",
                table: "employee_roles");

            migrationBuilder.CreateIndex(
                name: "ux_employee_roles_employee_id_role_code_scope",
                schema: "identity",
                table: "employee_roles",
                columns: new[] { "employee_id", "role_code", "scope_type", "scope_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_employee_roles_employee_id_role_code_scope",
                schema: "identity",
                table: "employee_roles");

            migrationBuilder.CreateIndex(
                name: "ux_employee_roles_employee_id_role_code_scope",
                schema: "identity",
                table: "employee_roles",
                columns: new[] { "employee_id", "role_code", "scope_type", "scope_id" },
                unique: true);
        }
    }
}
