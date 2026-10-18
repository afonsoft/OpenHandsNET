using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_user_id",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "service_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
