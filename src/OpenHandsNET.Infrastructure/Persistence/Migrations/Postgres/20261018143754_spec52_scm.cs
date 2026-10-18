using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec52_scm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pull_request_head_sha",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pull_request_number",
                table: "conversations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pull_request_state",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pull_request_url",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "scm_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    installation_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    pem_secret_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    webhook_secret_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    repos_json = table.Column<string>(type: "text", nullable: true),
                    app_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    app_slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    client_secret_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scm_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scm_connections_org_id_kind",
                table: "scm_connections",
                columns: new[] { "org_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_kind_delivery_id",
                table: "webhook_deliveries",
                columns: new[] { "kind", "delivery_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scm_connections");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "pull_request_head_sha",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "pull_request_number",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "pull_request_state",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "pull_request_url",
                table: "conversations");
        }
    }
}
