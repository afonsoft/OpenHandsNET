using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class review_finding_permalink_summary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "summary_text",
                table: "review_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permalink",
                table: "review_findings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary_text",
                table: "review_runs");

            migrationBuilder.DropColumn(
                name: "permalink",
                table: "review_findings");
        }
    }
}