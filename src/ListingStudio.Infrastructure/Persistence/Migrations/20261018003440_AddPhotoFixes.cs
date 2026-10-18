using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoFixes : Migration
    {
        private static readonly string[] PhotoKeyColumns = ["PropertyMediaId", "PropertyId", "OrganizationId"];
        private static readonly string[] PhotoKeyPrincipalColumns = ["Id", "PropertyId", "OrganizationId"];
        private static readonly string[] HistoryColumns = ["OrganizationId", "PropertyId", "PropertyMediaId", "RequestedAtUtc"];
        private static readonly string[] QueueColumns = ["Status", "NextAttemptAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppliedFixes",
                table: "PropertyMedia",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAtUtc",
                table: "PropertyMedia",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalBlobPath",
                table: "PropertyMedia",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OriginalFileSize",
                table: "PropertyMedia",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalHeight",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalMimeType",
                table: "PropertyMedia",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalWidth",
                table: "PropertyMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PropertyPhotoFixes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyMediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyPhotoFixes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyPhotoFixes_PropertyMedia_PropertyMediaId_PropertyId~",
                        columns: x => new { x.PropertyMediaId, x.PropertyId, x.OrganizationId },
                        principalTable: "PropertyMedia",
                        principalColumns: PhotoKeyPrincipalColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyPhotoFixes_OrganizationId_PropertyId_PropertyMediaI~",
                table: "PropertyPhotoFixes",
                columns: HistoryColumns);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyPhotoFixes_PropertyMediaId_PropertyId_OrganizationId",
                table: "PropertyPhotoFixes",
                columns: PhotoKeyColumns);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyPhotoFixes_Queue",
                table: "PropertyPhotoFixes",
                columns: QueueColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyPhotoFixes");

            migrationBuilder.DropColumn(
                name: "AppliedFixes",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "EditedAtUtc",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "OriginalBlobPath",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "OriginalFileSize",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "OriginalHeight",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "OriginalMimeType",
                table: "PropertyMedia");

            migrationBuilder.DropColumn(
                name: "OriginalWidth",
                table: "PropertyMedia");
        }
    }
}
