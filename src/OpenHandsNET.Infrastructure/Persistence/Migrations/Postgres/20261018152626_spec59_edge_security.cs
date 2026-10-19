using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec59_edge_security : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "input_guard_llm_role",
                table: "security_policies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "input_guard_mode",
                table: "security_policies",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ip_access_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    cidr = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ip_access_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ip_access_rules_scope",
                table: "ip_access_rules",
                column: "scope");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ip_access_rules");

            migrationBuilder.DropColumn(
                name: "input_guard_llm_role",
                table: "security_policies");

            migrationBuilder.DropColumn(
                name: "input_guard_mode",
                table: "security_policies");
        }
    }
}