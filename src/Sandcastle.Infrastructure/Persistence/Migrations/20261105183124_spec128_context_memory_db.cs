using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec128_context_memory_db : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "context_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_context_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_memories",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_memories", x => new { x.conversation_id, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_context_notes_conversation_id_created_at",
                table: "context_notes",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_memories_updated_at",
                table: "conversation_memories",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "context_notes");

            migrationBuilder.DropTable(
                name: "conversation_memories");
        }
    }
}