using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignVideoFixConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FixConfirmed",
                table: "CampaignVideoRevisions",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FixConfirmedAtUtc",
                table: "CampaignVideoRevisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FixFeedback",
                table: "CampaignVideoRevisions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FixConfirmed",
                table: "CampaignVideoRevisions");

            migrationBuilder.DropColumn(
                name: "FixConfirmedAtUtc",
                table: "CampaignVideoRevisions");

            migrationBuilder.DropColumn(
                name: "FixFeedback",
                table: "CampaignVideoRevisions");
        }
    }
}
