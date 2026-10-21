using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignNarrationLocalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptionLanguage",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "English");

            migrationBuilder.AddColumn<bool>(
                name: "LocalizeAgentCard",
                table: "CampaignGenerationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalizedCopyJson",
                table: "CampaignGenerationJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LongFormLocalizedCopyJson",
                table: "CampaignGenerationJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NarrationRegion",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UnitedStates");

            migrationBuilder.AddColumn<string>(
                name: "SpokenLanguage",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "English");

            migrationBuilder.AddColumn<string>(
                name: "VoiceAccent",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "American");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptionLanguage",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "LocalizeAgentCard",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "LocalizedCopyJson",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "LongFormLocalizedCopyJson",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "NarrationRegion",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "SpokenLanguage",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "VoiceAccent",
                table: "CampaignGenerationJobs");
        }
    }
}
