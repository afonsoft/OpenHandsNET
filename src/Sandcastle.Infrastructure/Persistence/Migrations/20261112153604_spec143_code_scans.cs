using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec143_code_scans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "automations",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "task");

            migrationBuilder.AddColumn<string>(
                name: "scan_config_json",
                table: "automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "scan_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    scan_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    start_line = table.Column<int>(type: "INTEGER", nullable: true),
                    end_line = table.Column<int>(type: "INTEGER", nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    confidence = table.Column<double>(type: "REAL", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    remediation_hint = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    remediation_state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    pr_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    payoff = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    source_conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_findings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    repositories_json = table.Column<string>(type: "TEXT", nullable: false),
                    goal = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    profile = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    effort = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("pk_scans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_created_at_ticks",
                table: "scan_findings",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_org_id_status",
                table: "scan_findings",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_path",
                table: "scan_findings",
                column: "path");

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_remediation_state",
                table: "scan_findings",
                column: "remediation_state");

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_repository_severity",
                table: "scan_findings",
                columns: new[] { "repository", "severity" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_findings_scan_id",
                table: "scan_findings",
                column: "scan_id");

            migrationBuilder.CreateIndex(
                name: "ix_scans_coordinator_conversation_id",
                table: "scans",
                column: "coordinator_conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_scans_created_at_ticks",
                table: "scans",
                column: "created_at_ticks",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_scans_org_id_status",
                table: "scans",
                columns: new[] { "org_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_findings");

            migrationBuilder.DropTable(
                name: "scans");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "scan_config_json",
                table: "automations");
        }
    }
}