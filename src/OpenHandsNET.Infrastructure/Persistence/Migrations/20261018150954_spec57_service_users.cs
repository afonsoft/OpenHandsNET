using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec57_service_users : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "created_via",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_user_id",
                table: "api_keys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "service_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_service_user_id",
                table: "api_keys",
                column: "service_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_users");

            migrationBuilder.DropIndex(
                name: "ix_api_keys_service_user_id",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "created_via",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "service_user_id",
                table: "api_keys");
        }
    }
}
