using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec141_ci_autofix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "autofix_check_patterns_json",
                table: "scm_connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "autofix_enabled",
                table: "scm_connections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "autofix_max_attempts",
                table: "scm_connections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "autofix_repos_json",
                table: "scm_connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "autofix_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    head_sha_in = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    head_sha_out = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    failed_checks_json = table.Column<string>(type: "TEXT", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    log_digest = table.Column<string>(type: "TEXT", nullable: true),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_autofix_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "autofix_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "INTEGER", nullable: false),
                    head_sha = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    head_branch = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    check_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    check_kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    check_run_id = table.Column<long>(type: "INTEGER", nullable: true),
                    workflow_run_id = table.Column<long>(type: "INTEGER", nullable: true),
                    check_url = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
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
                    table.PrimaryKey("pk_autofix_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_autofix_attempts_head_sha_out",
                table: "autofix_attempts",
                column: "head_sha_out");

            migrationBuilder.CreateIndex(
                name: "ix_autofix_attempts_run_id",
                table: "autofix_attempts",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_autofix_attempts_run_id_number",
                table: "autofix_attempts",
                columns: new[] { "run_id", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_autofix_runs_connection_id",
                table: "autofix_runs",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_autofix_runs_created_at_ticks",
                table: "autofix_runs",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_autofix_runs_org_id_status",
                table: "autofix_runs",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_autofix_runs_repository_pr_number_head_sha",
                table: "autofix_runs",
                columns: new[] { "repository", "pr_number", "head_sha" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autofix_attempts");

            migrationBuilder.DropTable(
                name: "autofix_runs");

            migrationBuilder.DropColumn(
                name: "autofix_check_patterns_json",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "autofix_enabled",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "autofix_max_attempts",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "autofix_repos_json",
                table: "scm_connections");
        }
    }
}