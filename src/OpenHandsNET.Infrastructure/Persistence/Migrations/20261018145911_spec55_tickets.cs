using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec55_tickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ticket_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ticket_kind",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ticket_url",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    api_url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    token_secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    webhook_secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    workspace_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    trigger_config_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_project_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    selected_repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    selected_branch = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_project_configs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_connections_kind_workspace_id",
                table: "ticket_connections",
                columns: new[] { "kind", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_project_configs_connection_id_project_key",
                table: "ticket_project_configs",
                columns: new[] { "connection_id", "project_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_connections");

            migrationBuilder.DropTable(
                name: "ticket_project_configs");

            migrationBuilder.DropColumn(
                name: "ticket_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ticket_kind",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ticket_url",
                table: "conversations");
        }
    }
}
