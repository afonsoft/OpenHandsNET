using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TimestampTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_ConversationId_Kind",
                table: "events",
                columns: new[] { "ConversationId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_events_ConversationId_TimestampTicks",
                table: "events",
                columns: new[] { "ConversationId", "TimestampTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
