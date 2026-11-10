using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "autofix_enabled",
                table: "scm_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "autofix_max_attempts",
                table: "scm_connections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "autofix_repos_json",
                table: "scm_connections",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "autofix_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    head_sha_in = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    head_sha_out = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    failed_checks_json = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    log_digest = table.Column<string>(type: "text", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_autofix_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "autofix_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    head_branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    check_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    check_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    check_run_id = table.Column<long>(type: "bigint", nullable: true),
                    workflow_run_id = table.Column<long>(type: "bigint", nullable: true),
                    check_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
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