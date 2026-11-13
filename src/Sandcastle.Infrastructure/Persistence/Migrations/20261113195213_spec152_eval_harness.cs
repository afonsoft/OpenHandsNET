using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec152_eval_harness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "sandbox_spec_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "eval_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    verdict = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    duration_s = table.Column<double>(type: "REAL", nullable: true),
                    tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    prompt_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    completion_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<decimal>(type: "TEXT", nullable: false),
                    log_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    task_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    suite = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    n_tasks = table.Column<int>(type: "INTEGER", nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    route_override = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    resolved_n = table.Column<int>(type: "INTEGER", nullable: false),
                    failed_n = table.Column<int>(type: "INTEGER", nullable: false),
                    error_n = table.Column<int>(type: "INTEGER", nullable: false),
                    timeout_n = table.Column<int>(type: "INTEGER", nullable: false),
                    score_pct = table.Column<double>(type: "REAL", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "TEXT", nullable: true),
                    actual_cost_usd = table.Column<decimal>(type: "TEXT", nullable: true),
                    dataset_path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    stats_json = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_created_at_ticks",
                table: "eval_results",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_org_id_verdict",
                table: "eval_results",
                columns: new[] { "org_id", "verdict" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_run_id",
                table: "eval_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_run_id_task_id",
                table: "eval_results",
                columns: new[] { "run_id", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_session_id",
                table: "eval_results",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_created_at_ticks",
                table: "eval_runs",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_org_id_status",
                table: "eval_runs",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_org_id_suite",
                table: "eval_runs",
                columns: new[] { "org_id", "suite" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_results");

            migrationBuilder.DropTable(
                name: "eval_runs");

            migrationBuilder.DropColumn(
                name: "sandbox_spec_id",
                table: "conversations");
        }
    }
}