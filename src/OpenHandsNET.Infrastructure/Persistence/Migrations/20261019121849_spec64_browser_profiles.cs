using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec64_browser_profiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "secrets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "browser_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    domain = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    ciphertext = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<string>(type: "TEXT", nullable: true),
                    shared = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_browser_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_browser_profiles_owner_id_domain",
                table: "browser_profiles",
                columns: new[] { "owner_id", "domain" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "browser_profiles");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "secrets");
        }
    }
}
