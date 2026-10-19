using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec63_knowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repo_index_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    start_line = table.Column<int>(type: "integer", nullable: false),
                    end_line = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    symbol_name = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "insight_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    scope_json = table.Column<string>(type: "text", nullable: true),
                    summary_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_insight_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: false),
                    repo_path = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    source_conversation_ids_json = table.Column<string>(type: "text", nullable: false),
                    source_entry_ids_json = table.Column<string>(type: "text", nullable: false),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_suggestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repo_indexes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repository = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    stats_json = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repo_indexes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_insights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    issues_json = table.Column<string>(type: "text", nullable: true),
                    dead_ends_json = table.Column<string>(type: "text", nullable: true),
                    phase_costs_json = table.Column<string>(type: "text", nullable: true),
                    suggested_prompt = table.Column<string>(type: "text", nullable: true),
                    learnings_json = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_insights", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_units_repo_index_id",
                table: "code_units",
                column: "repo_index_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_units_repo_index_id_path",
                table: "code_units",
                columns: new[] { "repo_index_id", "path" });

            migrationBuilder.CreateIndex(
                name: "ix_insight_reports_org_id",
                table: "insight_reports",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_repo_indexes_org_id_repository_branch",
                table: "repo_indexes",
                columns: new[] { "org_id", "repository", "branch" });

            migrationBuilder.CreateIndex(
                name: "ix_session_insights_conversation_id",
                table: "session_insights",
                column: "conversation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_insights_org_id_outcome",
                table: "session_insights",
                columns: new[] { "org_id", "outcome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_units");

            migrationBuilder.DropTable(
                name: "insight_reports");

            migrationBuilder.DropTable(
                name: "knowledge_suggestions");

            migrationBuilder.DropTable(
                name: "repo_indexes");

            migrationBuilder.DropTable(
                name: "session_insights");
        }
    }
}
