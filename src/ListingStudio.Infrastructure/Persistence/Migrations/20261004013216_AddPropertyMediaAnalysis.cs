using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyMediaAnalysis : Migration
    {
        private static readonly string[] AnalysisQueueColumns =
            ["AnalysisStatus", "AnalysisNextAttemptAtUtc", "AnalysisAttemptCount"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnalysisAttemptCount",
                table: "PropertyMedia",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnalysisCompletedAtUtc",
                table: "PropertyMedia",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalysisDescription",
                table: "PropertyMedia",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnalysisLastAttemptedAtUtc",
                table: "PropertyMedia",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalysisLastError",
                table: "PropertyMedia",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnalysisNextAttemptAtUtc",
                table: "PropertyMedia",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "PropertyMedia",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContainsPeople",
                table: "PropertyMedia",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HeroScore",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExterior",
                table: "PropertyMedia",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInterior",
                table: "PropertyMedia",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "PotentialProblems",
                table: "PropertyMedia",
                type: "text[]",
                nullable: false,
                defaultValue: Array.Empty<string>());

            migrationBuilder.AddColumn<int>(
                name: "QualityScore",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomType",
                table: "PropertyMedia",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuggestedDisplayOrder",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMedia_AnalysisQueue",
                table: "PropertyMedia",
                columns: AnalysisQueueColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyMedia_AnalysisQueue",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisAttemptCount",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisCompletedAtUtc",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisDescription",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisLastAttemptedAtUtc",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisLastError",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "AnalysisNextAttemptAtUtc",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "ContainsPeople",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "HeroScore",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "IsExterior",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "IsInterior",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "PotentialProblems",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "QualityScore",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "RoomType",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "SuggestedDisplayOrder",
                table: "PropertyMedia");
        }
    }
}
