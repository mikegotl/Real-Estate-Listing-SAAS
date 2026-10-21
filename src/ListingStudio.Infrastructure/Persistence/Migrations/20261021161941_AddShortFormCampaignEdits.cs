using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShortFormCampaignEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenHouseText",
                table: "CampaignGenerationJobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OpenHouseVerified",
                table: "CampaignGenerationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Pace",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Fast");

            migrationBuilder.AddColumn<string>(
                name: "ShortFormLocalizedCopyJson",
                table: "CampaignGenerationJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortFormVoiceover",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "ShortHook");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenHouseText",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "OpenHouseVerified",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "Pace",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "ShortFormLocalizedCopyJson",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "ShortFormVoiceover",
                table: "CampaignGenerationJobs");
        }
    }
}
