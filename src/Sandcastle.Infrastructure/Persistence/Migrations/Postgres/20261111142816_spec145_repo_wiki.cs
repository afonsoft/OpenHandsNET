using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec145_repo_wiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "repo_indexes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "repo_indexes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "wiki_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wiki_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    path_key = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content_md = table.Column<string>(type: "text", nullable: false),
                    source_files_json = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    stale = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_pages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wiki_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repository = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    effort = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    auto_update = table.Column<bool>(type: "boolean", nullable: false),
                    stats_json = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    requested_by_user_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at_ticks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wiki_pages_org_id_repository",
                table: "wiki_pages",
                columns: new[] { "org_id", "repository" });

            migrationBuilder.CreateIndex(
                name: "ix_wiki_pages_repository_branch_path_key",
                table: "wiki_pages",
                columns: new[] { "repository", "branch", "path_key" });

            migrationBuilder.CreateIndex(
                name: "ix_wiki_pages_wiki_run_id",
                table: "wiki_pages",
                column: "wiki_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_wiki_runs_created_at",
                table: "wiki_runs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_wiki_runs_org_id_repository_branch",
                table: "wiki_runs",
                columns: new[] { "org_id", "repository", "branch" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wiki_pages");

            migrationBuilder.DropTable(
                name: "wiki_runs");

            migrationBuilder.DropColumn(
                name: "created_at_ticks",
                table: "repo_indexes");

            migrationBuilder.DropColumn(
                name: "updated_at_ticks",
                table: "repo_indexes");
        }
    }
}