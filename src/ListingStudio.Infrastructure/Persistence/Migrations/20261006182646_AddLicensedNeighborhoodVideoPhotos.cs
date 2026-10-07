using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLicensedNeighborhoodVideoPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VideoPhotoBlobPath",
                table: "NeighborhoodInsights",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoPhotoCredit",
                table: "NeighborhoodInsights",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VideoPhotoFileSize",
                table: "NeighborhoodInsights",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoPhotoFilename",
                table: "NeighborhoodInsights",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoPhotoHeight",
                table: "NeighborhoodInsights",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoPhotoMimeType",
                table: "NeighborhoodInsights",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VideoPhotoUploadedAtUtc",
                table: "NeighborhoodInsights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoPhotoWidth",
                table: "NeighborhoodInsights",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VideoPhotoBlobPath",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoCredit",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoFileSize",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoFilename",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoHeight",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoMimeType",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoUploadedAtUtc",
                table: "NeighborhoodInsights");

            migrationBuilder.DropColumn(
                name: "VideoPhotoWidth",
                table: "NeighborhoodInsights");
        }
    }
}
