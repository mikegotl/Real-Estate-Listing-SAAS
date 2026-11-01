using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: a library campaign remembers the agent's goal and, when the agent picked their own videos, the
    /// formats in order. Both are null for every existing campaign; nothing is rewritten.
    /// </summary>
    public partial class AddVerticalCampaignGoal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerticalFormats",
                table: "CampaignGenerationJobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerticalGoal",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <summary>Development only. In production, roll back by turning VerticalVideoLibraryV1 off.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerticalFormats",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "VerticalGoal",
                table: "CampaignGenerationJobs");
        }
    }
}
