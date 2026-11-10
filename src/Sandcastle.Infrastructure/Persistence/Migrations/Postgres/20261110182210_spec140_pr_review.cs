using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "review_pass_condition",
                table: "scm_connections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_repos_json",
                table: "scm_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_trigger_mode",
                table: "scm_connections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    line = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    suggested_patch = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_findings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    diff_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    stats_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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