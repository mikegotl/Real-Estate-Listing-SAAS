using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerVideoKlingPresenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KlingAssetPath",
                table: "CampaignVideoVariants",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KlingDurationMs",
                table: "CampaignVideoVariants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "KlingFileSize",
                table: "CampaignVideoVariants",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KlingGenerationVersion",
                table: "CampaignVideoVariants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KlingProviderRequestId",
                table: "CampaignVideoVariants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KlingSubmittedAtUtc",
                table: "CampaignVideoVariants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KlingSubmittedPrompt",
                table: "CampaignVideoVariants",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativePresenterFormatScriptsJson",
                table: "CampaignGenerationJobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KlingAssetPath",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingDurationMs",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingFileSize",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingGenerationVersion",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingProviderRequestId",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingSubmittedAtUtc",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "KlingSubmittedPrompt",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "NativePresenterFormatScriptsJson",
                table: "CampaignGenerationJobs");
        }
    }
}
