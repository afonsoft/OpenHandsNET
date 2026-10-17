using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    preset = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    analyzer_override = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ask_mcp_tools = table.Column<bool>(type: "INTEGER", nullable: true),
                    min_confirm_risk = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    resume_strict = table.Column<bool>(type: "INTEGER", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_policies_org_id",
                table: "security_policies",
                column: "org_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_policies");
        }
    }
}
