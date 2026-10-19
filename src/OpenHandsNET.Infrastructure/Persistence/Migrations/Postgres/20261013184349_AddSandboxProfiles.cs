using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSandboxProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pool",
                table: "sandboxes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "pids_limit",
                table: "sandbox_specs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "profile",
                table: "sandbox_specs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sandbox_profile",
                table: "conversations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pool",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "pids_limit",
                table: "sandbox_specs");

            migrationBuilder.DropColumn(
                name: "profile",
                table: "sandbox_specs");

            migrationBuilder.DropColumn(
                name: "sandbox_profile",
                table: "conversations");
        }
    }
}