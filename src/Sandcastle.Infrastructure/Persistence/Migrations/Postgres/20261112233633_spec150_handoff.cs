using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec150_handoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "handoff_meta_json",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handoff_package_json",
                table: "conversations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handoff_meta_json",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "handoff_package_json",
                table: "conversations");
        }
    }
}