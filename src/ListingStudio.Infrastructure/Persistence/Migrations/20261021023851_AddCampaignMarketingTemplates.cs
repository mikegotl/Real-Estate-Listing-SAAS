using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignMarketingTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MarketingTemplate",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "BalancedShowcase");

            migrationBuilder.AddColumn<decimal>(
                name: "PriceReductionAmount",
                table: "CampaignGenerationJobs",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriceReductionVerified",
                table: "CampaignGenerationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarketingTemplate",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "PriceReductionAmount",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "PriceReductionVerified",
                table: "CampaignGenerationJobs");
        }
    }
}
