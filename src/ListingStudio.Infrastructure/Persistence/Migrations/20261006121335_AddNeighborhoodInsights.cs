using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNeighborhoodInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NeighborhoodInsights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderPlaceId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DistanceMiles = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    HasPhoto = table.Column<bool>(type: "boolean", nullable: false),
                    PhotoAttribution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PhotoAttributionUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PhotoSourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NeighborhoodInsights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NeighborhoodInsights_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NeighborhoodInsights_OrganizationId_PropertyId_IsApproved",
                table: "NeighborhoodInsights",
                columns: new[] { "OrganizationId", "PropertyId", "IsApproved" });

            migrationBuilder.CreateIndex(
                name: "IX_NeighborhoodInsights_OrganizationId_PropertyId_ProviderPlac~",
                table: "NeighborhoodInsights",
                columns: new[] { "OrganizationId", "PropertyId", "ProviderPlaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NeighborhoodInsights_PropertyId_OrganizationId",
                table: "NeighborhoodInsights",
                columns: new[] { "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NeighborhoodInsights");
        }
    }
}
