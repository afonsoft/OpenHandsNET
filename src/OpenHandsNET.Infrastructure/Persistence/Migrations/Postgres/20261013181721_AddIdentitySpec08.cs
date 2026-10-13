using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddIdentitySpec08 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "email_verified",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "idp_type",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "onboarding_done",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "tos_accepted_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quota_concurrent_max",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quota_daily_max",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings_json",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_at",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "expires_at_ticks",
                table: "org_invitations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ip",
                table: "audit_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_id",
                table: "audit_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_type",
                table: "audit_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "timestamp_ticks",
                table: "audit_events",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "user_agent",
                table: "audit_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "not_before",
                table: "api_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code_hash = table.Column<string>(type: "text", nullable: false),
                    user_code = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    denied = table.Column<bool>(type: "boolean", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    poll_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_flags", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_codes_device_code_hash",
                table: "device_codes",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_codes_expires_at",
                table: "device_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_codes_user_code",
                table: "device_codes",
                column: "user_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_codes");

            migrationBuilder.DropTable(
                name: "feature_flags");

            migrationBuilder.DropColumn(
                name: "email_verified",
                table: "users");

            migrationBuilder.DropColumn(
                name: "idp_type",
                table: "users");

            migrationBuilder.DropColumn(
                name: "onboarding_done",
                table: "users");

            migrationBuilder.DropColumn(
                name: "tos_accepted_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "quota_concurrent_max",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "quota_daily_max",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "settings_json",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "expires_at_ticks",
                table: "org_invitations");

            migrationBuilder.DropColumn(
                name: "ip",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "target_type",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "timestamp_ticks",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "user_agent",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "not_before",
                table: "api_keys");
        }
    }
}
