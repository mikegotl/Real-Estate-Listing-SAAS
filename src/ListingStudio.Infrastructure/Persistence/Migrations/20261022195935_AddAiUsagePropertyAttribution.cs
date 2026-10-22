using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsagePropertyAttribution : Migration
    {
        private static readonly string[] AiUsagePropertyIndexColumns = ["OccurredAtUtc", "PropertyReference"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PropertyReference",
                table: "AiUsageEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEvents_OccurredAtUtc_PropertyReference",
                table: "AiUsageEvents",
                columns: AiUsagePropertyIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageEvents_OccurredAtUtc_PropertyReference",
                table: "AiUsageEvents");

            migrationBuilder.DropColumn(
                name: "PropertyReference",
                table: "AiUsageEvents");
        }
    }
}
