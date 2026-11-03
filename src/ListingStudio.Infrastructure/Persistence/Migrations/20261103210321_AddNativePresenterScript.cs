using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativePresenterScript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NativePresenterClosing",
                table: "CampaignGenerationJobs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativePresenterOpening",
                table: "CampaignGenerationJobs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativePresenterTour",
                table: "CampaignGenerationJobs",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NativePresenterClosing",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "NativePresenterOpening",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "NativePresenterTour",
                table: "CampaignGenerationJobs");
        }
    }
}
