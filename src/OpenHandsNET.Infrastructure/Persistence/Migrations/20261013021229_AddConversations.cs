using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_start_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AppConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SandboxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_start_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SandboxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SelectedRepository = table.Column<string>(type: "TEXT", nullable: true),
                    AgentType = table.Column<string>(type: "TEXT", nullable: true),
                    LlmModel = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_start_tasks_Status",
                table: "conversation_start_tasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_CreatedAt",
                table: "conversations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_ExecutionStatus",
                table: "conversations",
                column: "ExecutionStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_start_tasks");

            migrationBuilder.DropTable(
                name: "conversations");
        }
    }
}