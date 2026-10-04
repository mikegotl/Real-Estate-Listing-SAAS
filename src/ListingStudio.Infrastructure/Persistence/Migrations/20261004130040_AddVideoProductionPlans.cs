using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoProductionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PropertyStories_Id_PropertyId_OrganizationId",
                table: "PropertyStories",
                columns: new[] { "Id", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "VideoProductionPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyStoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RequestedDuration = table.Column<int>(type: "integer", nullable: false),
                    AspectRatio = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DirectorVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpecificationJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoProductionPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoProductionPlans_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VideoProductionPlans_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VideoProductionPlans_PropertyStories_PropertyStoryId_Proper~",
                        columns: x => new { x.PropertyStoryId, x.PropertyId, x.OrganizationId },
                        principalTable: "PropertyStories",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoProductionPlans_OrganizationId_PropertyId_RequestedDur~",
                table: "VideoProductionPlans",
                columns: new[] { "OrganizationId", "PropertyId", "RequestedDuration", "AspectRatio", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoProductionPlans_OrganizationId_PropertyId_SourceFinger~",
                table: "VideoProductionPlans",
                columns: new[] { "OrganizationId", "PropertyId", "SourceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoProductionPlans_PropertyId_OrganizationId",
                table: "VideoProductionPlans",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoProductionPlans_PropertyStoryId_PropertyId_Organizatio~",
                table: "VideoProductionPlans",
                columns: new[] { "PropertyStoryId", "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoProductionPlans");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PropertyStories_Id_PropertyId_OrganizationId",
                table: "PropertyStories");
        }
    }
}
#pragma warning restore CA1861
