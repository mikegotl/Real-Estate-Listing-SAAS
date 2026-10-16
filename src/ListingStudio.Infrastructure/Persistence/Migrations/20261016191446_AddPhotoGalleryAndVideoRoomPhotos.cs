using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoGalleryAndVideoRoomPhotos : Migration
    {
        private static readonly string[] FrameExtractionQueueColumns =
            ["FrameExtractionStatus", "FrameExtractionNextAttemptAtUtc", "FrameExtractionAttemptCount"];

        private static readonly string[] SourceVideoColumns = ["OrganizationId", "PropertyId", "SourceVideoId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionAttemptCount",
                table: "PropertyVideos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrameExtractionCompletedAtUtc",
                table: "PropertyVideos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrameExtractionLastAttemptedAtUtc",
                table: "PropertyVideos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrameExtractionLastError",
                table: "PropertyVideos",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrameExtractionNextAttemptAtUtc",
                table: "PropertyVideos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionPhotoCount",
                table: "PropertyVideos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrameExtractionRequestedAtUtc",
                table: "PropertyVideos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionRoomCount",
                table: "PropertyVideos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionSkippedPhotoCount",
                table: "PropertyVideos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrameExtractionStage",
                table: "PropertyVideos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrameExtractionStatus",
                table: "PropertyVideos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotRequested");

            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionStepsDone",
                table: "PropertyVideos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FrameExtractionStepsTotal",
                table: "PropertyVideos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsInGallery",
                table: "PropertyMedia",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceVideoId",
                table: "PropertyMedia",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceVideoTimestampMs",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_FrameExtractionQueue",
                table: "PropertyVideos",
                columns: FrameExtractionQueueColumns);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMedia_OrganizationId_PropertyId_SourceVideoId",
                table: "PropertyMedia",
                columns: SourceVideoColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyVideos_FrameExtractionQueue",
                table: "PropertyVideos");

            migrationBuilder.DropIndex(
                name: "IX_PropertyMedia_OrganizationId_PropertyId_SourceVideoId",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "FrameExtractionAttemptCount",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionCompletedAtUtc",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionLastAttemptedAtUtc",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionLastError",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionNextAttemptAtUtc",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionPhotoCount",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionRequestedAtUtc",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionRoomCount",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionSkippedPhotoCount",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionStage",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionStatus",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionStepsDone",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "FrameExtractionStepsTotal",
                table: "PropertyVideos");

            migrationBuilder.DropColumn(
                name: "IsInGallery",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "SourceVideoId",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "SourceVideoTimestampMs",
                table: "PropertyMedia");
        }
    }
}
