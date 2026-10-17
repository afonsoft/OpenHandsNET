using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_hooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    automation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_hooks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    automation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    started_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    prompt = table.Column<string>(type: "TEXT", nullable: false),
                    llm_model = table.Column<string>(type: "TEXT", nullable: true),
                    selected_repository = table.Column<string>(type: "TEXT", nullable: true),
                    schedule_kind = table.Column<string>(type: "TEXT", nullable: false),
                    schedule_time_utc = table.Column<string>(type: "TEXT", nullable: true),
                    interval_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    weekday = table.Column<int>(type: "INTEGER", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    next_run_at_ticks = table.Column<long>(type: "INTEGER", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_hooks_automation_id",
                table: "automation_hooks",
                column: "automation_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_hooks_token_hash",
                table: "automation_hooks",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_automation_id_started_at_ticks",
                table: "automation_runs",
                columns: new[] { "automation_id", "started_at_ticks" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_status",
                table: "automation_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_automations_created_by_user_id",
                table: "automations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_automations_enabled_next_run_at_ticks",
                table: "automations",
                columns: new[] { "enabled", "next_run_at_ticks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_hooks");

            migrationBuilder.DropTable(
                name: "automation_runs");

            migrationBuilder.DropTable(
                name: "automations");
        }
    }
}
