using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKling4FullTourStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FullTourAssetPath",
                table: "CampaignGenerationJobs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullTourContentType",
                table: "CampaignGenerationJobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FullTourDurationMs",
                table: "CampaignGenerationJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FullTourFileSize",
                table: "CampaignGenerationJobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullTourGenerationVersion",
                table: "CampaignGenerationJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullTourProviderRequestId",
                table: "CampaignGenerationJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FullTourSubmittedAtUtc",
                table: "CampaignGenerationJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoStrategy",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Established");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullTourAssetPath",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourContentType",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourDurationMs",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourFileSize",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourGenerationVersion",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourProviderRequestId",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "FullTourSubmittedAtUtc",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "VideoStrategy",
                table: "CampaignGenerationJobs");
        }
    }
}
