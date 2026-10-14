using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "build");

            migrationBuilder.AddColumn<string>(
                name: "permissions_json",
                table: "conversations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mode",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "permissions_json",
                table: "conversations");
        }
    }
}
