using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SandboxBlueprintFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blueprint_env_json",
                table: "sandboxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blueprint_image",
                table: "sandboxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blueprint_log_tail",
                table: "sandboxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blueprint_status",
                table: "sandboxes",
                type: "TEXT",
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "pool_key",
                table: "sandboxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_key_protected",
                table: "sandboxes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blueprint_env_json",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "blueprint_image",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "blueprint_log_tail",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "blueprint_status",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "pool_key",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "session_key_protected",
                table: "sandboxes");
        }
    }
}
