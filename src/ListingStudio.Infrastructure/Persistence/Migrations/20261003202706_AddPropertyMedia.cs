using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyMedia : Migration
    {
        private static readonly string[] PropertyTenantKeyColumns = ["Id", "OrganizationId"];
        private static readonly string[] MediaPropertyTenantColumns = ["PropertyId", "OrganizationId"];
        private static readonly string[] MediaOrderColumns = ["OrganizationId", "PropertyId", "DisplayOrder"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Properties_Id_OrganizationId",
                table: "Properties",
                columns: PropertyTenantKeyColumns);

            migrationBuilder.CreateTable(
                name: "PropertyMedia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnalysisStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyMedia_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyTenantKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMedia_BlobPath",
                table: "PropertyMedia",
                column: "BlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMedia_OrganizationId_PropertyId_DisplayOrder",
                table: "PropertyMedia",
                columns: MediaOrderColumns);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMedia_PropertyId_OrganizationId",
                table: "PropertyMedia",
                columns: MediaPropertyTenantColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyMedia");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Properties_Id_OrganizationId",
                table: "Properties");
        }
    }
}
