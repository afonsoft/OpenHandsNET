using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SandboxUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "sandboxes",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "sandboxes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill: linhas existentes herdam created_at — um paused antigo
            // não pode virar elegível ao stop-reaper com epoch zero.
            migrationBuilder.Sql(
                "UPDATE sandboxes SET updated_at = created_at, " +
                "updated_at_ticks = created_at_ticks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "updated_at_ticks",
                table: "sandboxes");
        }
    }
}