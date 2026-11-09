using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec134_user_llm_keys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_llm_keys",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: false),
                    key_ciphertext = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<string>(type: "TEXT", nullable: false),
                    base_url = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_llm_keys", x => new { x.user_id, x.provider });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_llm_keys");
        }
    }
}