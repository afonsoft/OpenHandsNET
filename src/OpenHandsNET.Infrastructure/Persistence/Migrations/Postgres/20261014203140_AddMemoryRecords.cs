using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMemoryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memory_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memory_records_org_id_key",
                table: "memory_records",
                columns: new[] { "org_id", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_memory_records_updated_at",
                table: "memory_records",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_records");
        }
    }
}
