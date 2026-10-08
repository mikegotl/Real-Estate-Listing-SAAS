using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline index/constraint column arrays.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyNarrationScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyNarrationScripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    ExtractedText = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MarketingUseAccepted = table.Column<bool>(type: "boolean", nullable: false),
                    MarketingUseAcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MarketingUseAcceptedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyNarrationScripts", x => x.Id);
                    table.UniqueConstraint("AK_PropertyNarrationScripts_Id_PropertyId_OrganizationId", x => new { x.Id, x.PropertyId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_PropertyNarrationScripts_Properties_PropertyId_Organization~",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyNarrationScripts_BlobPath",
                table: "PropertyNarrationScripts",
                column: "BlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyNarrationScripts_OrganizationId_PropertyId",
                table: "PropertyNarrationScripts",
                columns: new[] { "OrganizationId", "PropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyNarrationScripts_PropertyId_OrganizationId",
                table: "PropertyNarrationScripts",
                columns: new[] { "PropertyId", "OrganizationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyNarrationScripts");
        }
    }
}
