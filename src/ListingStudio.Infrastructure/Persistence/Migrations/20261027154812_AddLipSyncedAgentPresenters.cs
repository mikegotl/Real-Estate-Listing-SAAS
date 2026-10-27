using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLipSyncedAgentPresenters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentAvatarVideos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentPortraitId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoNarrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IntroTimelineStartMs = table.Column<int>(type: "integer", nullable: false),
                    IntroDurationMs = table.Column<int>(type: "integer", nullable: false),
                    OutroTimelineStartMs = table.Column<int>(type: "integer", nullable: false),
                    OutroDurationMs = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GenerationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProviderMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAvatarVideos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentAvatarVideos_AgentPortraits_AgentPortraitId_Organizati~",
                        columns: x => new { x.AgentPortraitId, x.OrganizationId },
                        principalTable: "AgentPortraits",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentAvatarVideos_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentAvatarVideos_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentAvatarVideos_VideoNarrations_VideoNarrationId_Property~",
                        columns: x => new { x.VideoNarrationId, x.PropertyId, x.OrganizationId },
                        principalTable: "VideoNarrations",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_AgentPortraitId_OrganizationId",
                table: "AgentAvatarVideos",
                columns: new[] { "AgentPortraitId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_AssetPath",
                table: "AgentAvatarVideos",
                column: "AssetPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_OrganizationId_PropertyId_CreatedAtUtc",
                table: "AgentAvatarVideos",
                columns: new[] { "OrganizationId", "PropertyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_OrganizationId_PropertyId_SourceFingerpri~",
                table: "AgentAvatarVideos",
                columns: new[] { "OrganizationId", "PropertyId", "SourceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_PropertyId_OrganizationId",
                table: "AgentAvatarVideos",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAvatarVideos_VideoNarrationId_PropertyId_OrganizationId",
                table: "AgentAvatarVideos",
                columns: new[] { "VideoNarrationId", "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAvatarVideos");
        }
    }
}
#pragma warning restore CA1861
