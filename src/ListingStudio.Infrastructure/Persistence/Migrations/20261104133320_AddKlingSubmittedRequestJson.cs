using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKlingSubmittedRequestJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KlingSubmittedRequestJson",
                table: "CampaignVideoVariants",
                type: "character varying(40000)",
                maxLength: 40000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullTourSubmittedRequestJson",
                table: "CampaignGenerationJobs",
                type: "character varying(40000)",
                maxLength: 40000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KlingSubmittedRequestJson",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "FullTourSubmittedRequestJson",
                table: "CampaignGenerationJobs");
        }
    }
}
