using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTickColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_created_at",
                table: "conversations");

            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "sandboxes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "expires_at_ticks",
                table: "sandboxes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "conversation_start_tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "conversation_start_tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_created_at_ticks",
                table: "conversations",
                column: "created_at_ticks");

            // Backfill: timestamptz → UtcTicks (epoch numeric = exata).
            const string ticksExpr =
                "CAST((EXTRACT(EPOCH FROM {0}) + 62135596800) * 10000000 AS bigint)";
            foreach (var (table, col) in new[]
            {
                ("conversations", "created_at"), ("conversations", "updated_at"),
                ("conversation_start_tasks", "created_at"),
                ("conversation_start_tasks", "updated_at"),
                ("sandboxes", "created_at"),
            })
            {
                migrationBuilder.Sql(
                    $"UPDATE {table} SET {col}_ticks = "
                    + string.Format(ticksExpr, col));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_created_at_ticks",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "created_at_ticks",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "expires_at_ticks",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "created_at_ticks",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "updated_at_ticks",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "created_at_ticks",
                table: "conversation_start_tasks");

            migrationBuilder.DropColumn(
                name: "updated_at_ticks",
                table: "conversation_start_tasks");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_created_at",
                table: "conversations",
                column: "created_at");
        }
    }
}