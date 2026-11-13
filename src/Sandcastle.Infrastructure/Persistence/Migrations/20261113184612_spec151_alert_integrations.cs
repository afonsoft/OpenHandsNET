using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec151_alert_integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "team_id",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "signing_secret_ref",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "bot_token_secret_ref",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "slack");

            migrationBuilder.AddColumn<string>(
                name: "webhook_url",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    external_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    service = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    culprit = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    labels_json = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    pr_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alert_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    repo_mapping_json = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    auto_create_session = table.Column<bool>(type: "INTEGER", nullable: false),
                    triage_severity_min = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_sources", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_created_at_ticks",
                table: "alert_events",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_org_id_status",
                table: "alert_events",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_session_id",
                table: "alert_events",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_source_id_external_id",
                table: "alert_events",
                columns: new[] { "source_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_sources_org_id",
                table: "alert_sources",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "alert_sources");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "chat_connections");

            migrationBuilder.DropColumn(
                name: "webhook_url",
                table: "chat_connections");

            migrationBuilder.AlterColumn<string>(
                name: "team_id",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "signing_secret_ref",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "bot_token_secret_ref",
                table: "chat_connections",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}