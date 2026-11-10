using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec140_pr_review : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "review_enabled",
                table: "scm_connections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "review_pass_condition",
                table: "scm_connections",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_repos_json",
                table: "scm_connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_trigger_mode",
                table: "scm_connections",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    review_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    confidence = table.Column<double>(type: "REAL", nullable: true),
                    path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    line = table.Column<int>(type: "INTEGER", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    suggested_patch = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    comment_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_findings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "INTEGER", nullable: false),
                    head_sha = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    diff_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    partial = table.Column<bool>(type: "INTEGER", nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    stats_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_findings_repository_pr_number",
                table: "review_findings",
                columns: new[] { "repository", "pr_number" });

            migrationBuilder.CreateIndex(
                name: "ix_review_findings_review_run_id",
                table: "review_findings",
                column: "review_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_findings_status",
                table: "review_findings",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_review_runs_connection_id",
                table: "review_runs",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_runs_created_at_ticks",
                table: "review_runs",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_review_runs_org_id_status",
                table: "review_runs",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_review_runs_repository_pr_number_head_sha",
                table: "review_runs",
                columns: new[] { "repository", "pr_number", "head_sha" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_findings");

            migrationBuilder.DropTable(
                name: "review_runs");

            migrationBuilder.DropColumn(
                name: "review_enabled",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "review_pass_condition",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "review_repos_json",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "review_trigger_mode",
                table: "scm_connections");
        }
    }
}