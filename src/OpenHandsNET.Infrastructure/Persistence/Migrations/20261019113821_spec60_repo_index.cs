using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec60_repo_index : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ask_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    repo_index_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    repository = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    messages_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    promoted_conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ask_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "code_units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    repo_index_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    start_line = table.Column<int>(type: "INTEGER", nullable: false),
                    end_line = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: false),
                    hash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repo_indexes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    repository = table.Column<string>(type: "TEXT", nullable: false),
                    branch = table.Column<string>(type: "TEXT", nullable: false),
                    commit_sha = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    stats_json = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    indexed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repo_indexes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_units_repo_index_id_path",
                table: "code_units",
                columns: new[] { "repo_index_id", "path" });

            // SPEC-60: FTS5 full-text index sobre code_units (só SQLite).
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE IF NOT EXISTS code_units_fts "
                + "USING fts5(unit_id UNINDEXED, repo_index_id UNINDEXED, "
                + "path, text);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ask_sessions");

            migrationBuilder.DropTable(
                name: "code_units");

            migrationBuilder.DropTable(
                name: "repo_indexes");

            migrationBuilder.Sql("DROP TABLE IF EXISTS code_units_fts;");
        }
    }
}
