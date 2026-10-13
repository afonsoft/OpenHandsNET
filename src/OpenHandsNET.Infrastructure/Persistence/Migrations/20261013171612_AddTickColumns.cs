using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "expires_at_ticks",
                table: "sandboxes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "created_at_ticks",
                table: "conversation_start_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "updated_at_ticks",
                table: "conversation_start_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_created_at_ticks",
                table: "conversations",
                column: "created_at_ticks");
        
            // Backfill: converte o TEXT ISO-8601 do DateTimeOffset em UtcTicks
            // ((unix_s + 62135596800) * 1e7 + fração de segundo * 1e7).
            const string ticksExpr =
                "(CAST(strftime('%s', {0}) AS INTEGER) + 62135596800) * 10000000"
                + " + CAST((CAST(strftime('%f', {0}) AS REAL)"
                + " - CAST(strftime('%S', {0}) AS INTEGER)) * 10000000 AS INTEGER)";
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
