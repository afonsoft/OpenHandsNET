using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec58_scim_idp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "org_members",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "usage_tier_id",
                table: "org_members",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "idp_group_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    idp_group = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    usage_tier_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idp_group_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scim_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    prefix = table.Column<string>(type: "TEXT", nullable: false),
                    key_hash = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scim_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_tiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    limits_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_tiers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_usage_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    limits_json = table.Column<string>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_usage_overrides", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idp_group_mappings_org_id_idp_group",
                table: "idp_group_mappings",
                columns: new[] { "org_id", "idp_group" });

            migrationBuilder.CreateIndex(
                name: "ix_scim_tokens_org_id",
                table: "scim_tokens",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_usage_overrides_org_id_user_id",
                table: "user_usage_overrides",
                columns: new[] { "org_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idp_group_mappings");

            migrationBuilder.DropTable(
                name: "scim_tokens");

            migrationBuilder.DropTable(
                name: "usage_tiers");

            migrationBuilder.DropTable(
                name: "user_usage_overrides");

            migrationBuilder.DropColumn(
                name: "source",
                table: "org_members");

            migrationBuilder.DropColumn(
                name: "usage_tier_id",
                table: "org_members");
        }
    }
}
