using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoNarrationAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_VideoProductionPlans_Id_PropertyId_OrganizationId",
                table: "VideoProductionPlans",
                columns: new[] { "Id", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "VideoNarrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoProductionPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    GenerationVersion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    TimingJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoNarrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoNarrations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VideoNarrations_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VideoNarrations_VideoProductionPlans_VideoProductionPlanId_~",
                        columns: x => new { x.VideoProductionPlanId, x.PropertyId, x.OrganizationId },
                        principalTable: "VideoProductionPlans",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoNarrations_AssetPath",
                table: "VideoNarrations",
                column: "AssetPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoNarrations_OrganizationId_VideoProductionPlanId_Source~",
                table: "VideoNarrations",
                columns: new[] { "OrganizationId", "VideoProductionPlanId", "SourceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoNarrations_OrganizationId_VideoProductionPlanId_Version",
                table: "VideoNarrations",
                columns: new[] { "OrganizationId", "VideoProductionPlanId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoNarrations_PropertyId_OrganizationId",
                table: "VideoNarrations",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoNarrations_VideoProductionPlanId_PropertyId_Organizati~",
                table: "VideoNarrations",
                columns: new[] { "VideoProductionPlanId", "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoNarrations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_VideoProductionPlans_Id_PropertyId_OrganizationId",
                table: "VideoProductionPlans");
        }
    }
}
#pragma warning restore CA1861
