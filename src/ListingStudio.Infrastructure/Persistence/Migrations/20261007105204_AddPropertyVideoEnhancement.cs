using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline index/constraint column arrays.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyVideoEnhancement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyVideos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OriginalMimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OriginalFileSize = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    FrameRate = table.Column<decimal>(type: "numeric(8,3)", precision: 8, scale: 3, nullable: false),
                    HasAudio = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessingStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessingAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessingLastAttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingNextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingLastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EnhancedBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    EnhancedFileSize = table.Column<long>(type: "bigint", nullable: true),
                    EnhancedWidth = table.Column<int>(type: "integer", nullable: true),
                    EnhancedHeight = table.Column<int>(type: "integer", nullable: true),
                    EnhancedDurationMs = table.Column<int>(type: "integer", nullable: true),
                    EnhancedFrameRate = table.Column<decimal>(type: "numeric(8,3)", precision: 8, scale: 3, nullable: true),
                    EnhancementVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyVideos", x => x.Id);
                    table.UniqueConstraint("AK_PropertyVideos_Id_PropertyId_OrganizationId", x => new { x.Id, x.PropertyId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_PropertyVideos_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_EnhancedBlobPath",
                table: "PropertyVideos",
                column: "EnhancedBlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_OrganizationId_PropertyId_UploadedAtUtc",
                table: "PropertyVideos",
                columns: new[] { "OrganizationId", "PropertyId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_OriginalBlobPath",
                table: "PropertyVideos",
                column: "OriginalBlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_ProcessingQueue",
                table: "PropertyVideos",
                columns: new[] { "ProcessingStatus", "ProcessingNextAttemptAtUtc", "ProcessingAttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyVideos_PropertyId_OrganizationId",
                table: "PropertyVideos",
                columns: new[] { "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyVideos");
        }
    }
}
#pragma warning restore CA1861
