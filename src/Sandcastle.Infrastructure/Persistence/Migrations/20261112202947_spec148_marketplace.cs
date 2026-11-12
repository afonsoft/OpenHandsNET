using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec148_marketplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installed_skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    scope_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    entry_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    installed_by = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    installed_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    meta_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installed_skills", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_installed_skills_scope_scope_id_entry_id",
                table: "installed_skills",
                columns: new[] { "scope", "scope_id", "entry_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installed_skills");
        }
    }
}