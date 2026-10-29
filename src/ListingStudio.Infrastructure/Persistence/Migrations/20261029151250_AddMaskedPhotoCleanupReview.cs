using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskedPhotoCleanupReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateBlobPath",
                table: "PropertyPhotoFixes",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CandidateFileSize",
                table: "PropertyPhotoFixes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CandidateHeight",
                table: "PropertyPhotoFixes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateMimeType",
                table: "PropertyPhotoFixes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CandidateWidth",
                table: "PropertyPhotoFixes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditMask",
                table: "PropertyPhotoFixes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StagedAtUtc",
                table: "PropertyPhotoFixes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateBlobPath",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "CandidateFileSize",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "CandidateHeight",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "CandidateMimeType",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "CandidateWidth",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "EditMask",
                table: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "StagedAtUtc",
                table: "PropertyPhotoFixes");
        }
    }
}
