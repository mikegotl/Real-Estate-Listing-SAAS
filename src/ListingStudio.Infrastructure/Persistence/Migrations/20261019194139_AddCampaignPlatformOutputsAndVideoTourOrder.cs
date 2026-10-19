using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignPlatformOutputsAndVideoTourOrder : Migration
    {
        private static readonly string[] TourIndexColumns = ["OrganizationId", "PropertyId", "TourOrder"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TourOrder",
                table: "PropertyVideos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TourStop",
                table: "PropertyVideos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedOutputs",
                table: "CampaignGenerationJobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "LegacyStandard");

            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "OrganizationId", "PropertyId"
                               ORDER BY "UploadedAtUtc", "Id") - 1 AS position
                    FROM "PropertyVideos"
                )
                UPDATE "PropertyVideos" AS video
                SET "TourOrder" = ordered.position
                FROM ordered
                WHERE video."Id" = ordered."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_OrganizationId_PropertyId_TourOrder",
                table: "PropertyVideos",
                columns: TourIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyVideos_OrganizationId_PropertyId_TourOrder",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "TourOrder",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "TourStop",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "RequestedOutputs",
                table: "CampaignGenerationJobs");
        }
    }
}
