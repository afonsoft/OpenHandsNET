using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSandboxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sandbox_specs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MemoryMb = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuLimit = table.Column<double>(type: "REAL", nullable: false),
                    TimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvJson = table.Column<string>(type: "TEXT", nullable: true),
                    MountWorkspace = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_specs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sandboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpecId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AgentUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandboxes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sandboxes_ConversationId",
                table: "sandboxes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_sandboxes_Status",
                table: "sandboxes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sandbox_specs");

            migrationBuilder.DropTable(
                name: "sandboxes");
        }
    }
}
