using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec54_chatops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_channel_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    selected_repository = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    selected_branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    auto_triage = table.Column<bool>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_channel_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    team_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    bot_token_secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    signing_secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    bot_user_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    default_channel_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_thread_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    thread_ts = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status_message_ts = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_thread_bindings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_channel_configs_connection_id_channel_id",
                table: "chat_channel_configs",
                columns: new[] { "connection_id", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_connections_org_id",
                table: "chat_connections",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_connections_team_id",
                table: "chat_connections",
                column: "team_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_thread_bindings_connection_id_channel_id_thread_ts",
                table: "chat_thread_bindings",
                columns: new[] { "connection_id", "channel_id", "thread_ts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_thread_bindings_conversation_id",
                table: "chat_thread_bindings",
                column: "conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_channel_configs");

            migrationBuilder.DropTable(
                name: "chat_connections");

            migrationBuilder.DropTable(
                name: "chat_thread_bindings");
        }
    }
}