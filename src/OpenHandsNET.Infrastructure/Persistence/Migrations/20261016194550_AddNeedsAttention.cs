using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNeedsAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "needs_attention_at_ticks",
                table: "conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "needs_attention_kind",
                table: "conversations",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_org_id_needs_attention_at_ticks",
                table: "conversations",
                columns: new[] { "org_id", "needs_attention_at_ticks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_org_id_needs_attention_at_ticks",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "needs_attention_at_ticks",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "needs_attention_kind",
                table: "conversations");
        }
    }
}