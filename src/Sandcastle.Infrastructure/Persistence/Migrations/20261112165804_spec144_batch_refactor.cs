using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec144_batch_refactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refactor_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    plan_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    n = table.Column<int>(type: "INTEGER", nullable: false),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    files_json = table.Column<string>(type: "TEXT", nullable: false),
                    depends_on_json = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    risk = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    verify_command = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    pr_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    pr_number = table.Column<int>(type: "INTEGER", nullable: true),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    verify_log_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refactor_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refactor_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    repositories_json = table.Column<string>(type: "TEXT", nullable: false),
                    goal = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    strategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    requested_mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    max_concurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    batch_size_hint = table.Column<int>(type: "INTEGER", nullable: true),
                    plan_json = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    planner_conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    coordinator_conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    stats_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refactor_plans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refactor_batches_plan_id",
                table: "refactor_batches",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_refactor_batches_plan_id_n",
                table: "refactor_batches",
                columns: new[] { "plan_id", "n" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refactor_batches_repository_pr_number",
                table: "refactor_batches",
                columns: new[] { "repository", "pr_number" });

            migrationBuilder.CreateIndex(
                name: "ix_refactor_batches_state",
                table: "refactor_batches",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_refactor_plans_coordinator_conversation_id",
                table: "refactor_plans",
                column: "coordinator_conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_refactor_plans_created_at_ticks",
                table: "refactor_plans",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_refactor_plans_org_id_status",
                table: "refactor_plans",
                columns: new[] { "org_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refactor_batches");

            migrationBuilder.DropTable(
                name: "refactor_plans");
        }
    }
}