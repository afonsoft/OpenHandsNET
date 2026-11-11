using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec142_qa_agent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "qa_enabled",
                table: "scm_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "qa_pass_condition",
                table: "scm_connections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "qa_repos_json",
                table: "scm_connections",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "qa_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    base_sha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    plan_json = table.Column<string>(type: "text", nullable: true),
                    partial_plan = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_qa_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qa_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qa_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    artifact_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qa_steps", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_qa_runs_connection_id",
                table: "qa_runs",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_qa_runs_created_at_ticks",
                table: "qa_runs",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_qa_runs_org_id_status",
                table: "qa_runs",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_qa_runs_repository_pr_number_head_sha",
                table: "qa_runs",
                columns: new[] { "repository", "pr_number", "head_sha" });

            migrationBuilder.CreateIndex(
                name: "ix_qa_steps_qa_run_id",
                table: "qa_steps",
                column: "qa_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_qa_steps_qa_run_id_number",
                table: "qa_steps",
                columns: new[] { "qa_run_id", "number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qa_runs");

            migrationBuilder.DropTable(
                name: "qa_steps");

            migrationBuilder.DropColumn(
                name: "qa_enabled",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "qa_pass_condition",
                table: "scm_connections");

            migrationBuilder.DropColumn(
                name: "qa_repos_json",
                table: "scm_connections");
        }
    }
}