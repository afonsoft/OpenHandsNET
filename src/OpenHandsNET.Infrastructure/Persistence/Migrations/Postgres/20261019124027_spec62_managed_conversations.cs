using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec62_managed_conversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "delegation_depth",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "delegation_meta_json",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_conversation_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_parent_conversation_id",
                table: "conversations",
                column: "parent_conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_parent_conversation_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "delegation_depth",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "delegation_meta_json",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "parent_conversation_id",
                table: "conversations");
        }
    }
}
