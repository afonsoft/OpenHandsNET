using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSkillsHooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "skills",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "enabled",
                table: "skills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "org_id",
                table: "skills",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "skills",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "conversation_skills",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_skills", x => new { x.conversation_id, x.skill_id });
                });

            migrationBuilder.CreateTable(
                name: "hook_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: true),
                    org_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: true),
                    blocking = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hook_configs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hook_configs_owner_id_event",
                table: "hook_configs",
                columns: new[] { "owner_id", "event" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_skills");

            migrationBuilder.DropTable(
                name: "hook_configs");

            migrationBuilder.DropColumn(
                name: "description",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "enabled",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "org_id",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "skills");
        }
    }
}