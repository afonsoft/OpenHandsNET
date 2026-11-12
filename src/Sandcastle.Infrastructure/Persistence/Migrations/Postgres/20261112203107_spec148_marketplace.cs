using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
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
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    entry_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    installed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    installed_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    meta_json = table.Column<string>(type: "text", nullable: true)
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