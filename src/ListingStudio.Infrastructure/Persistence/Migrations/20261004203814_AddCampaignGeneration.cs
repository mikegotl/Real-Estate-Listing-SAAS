using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_VideoNarrations_Id_PropertyId_OrganizationId",
                table: "VideoNarrations",
                columns: new[] { "Id", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "CampaignGenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StageAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    TotalStageAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MasterVideoProductionPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    VideoNarrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SocialCaptionLong = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SocialCaptionShort = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignGenerationJobs", x => x.Id);
                    table.UniqueConstraint("AK_CampaignGenerationJobs_Id_PropertyId_OrganizationId", x => new { x.Id, x.PropertyId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_CampaignGenerationJobs_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignGenerationJobs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignGenerationJobs_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignGenerationJobs_VideoNarrations_VideoNarrationId_Pro~",
                        columns: x => new { x.VideoNarrationId, x.PropertyId, x.OrganizationId },
                        principalTable: "VideoNarrations",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignGenerationJobs_VideoProductionPlans_MasterVideoProd~",
                        columns: x => new { x.MasterVideoProductionPlanId, x.PropertyId, x.OrganizationId },
                        principalTable: "VideoProductionPlans",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CampaignDeliverables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedDuration = table.Column<int>(type: "integer", nullable: false),
                    AspectRatio = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpecificationJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RenderedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignDeliverables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignDeliverables_CampaignGenerationJobs_CampaignGenerat~",
                        columns: x => new { x.CampaignGenerationJobId, x.PropertyId, x.OrganizationId },
                        principalTable: "CampaignGenerationJobs",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_AssetPath",
                table: "CampaignDeliverables",
                column: "AssetPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_CampaignGenerationJobId_PropertyId_Org~",
                table: "CampaignDeliverables",
                columns: new[] { "CampaignGenerationJobId", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_OrganizationId_CampaignGenerationJobId~",
                table: "CampaignDeliverables",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_MasterVideoProductionPlanId_Property~",
                table: "CampaignGenerationJobs",
                columns: new[] { "MasterVideoProductionPlanId", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_OrganizationId_PropertyId",
                table: "CampaignGenerationJobs",
                columns: new[] { "OrganizationId", "PropertyId" },
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_OrganizationId_PropertyId_CreatedAtU~",
                table: "CampaignGenerationJobs",
                columns: new[] { "OrganizationId", "PropertyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_OrganizationId_PropertyId_SourceFing~",
                table: "CampaignGenerationJobs",
                columns: new[] { "OrganizationId", "PropertyId", "SourceFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_PropertyId_OrganizationId",
                table: "CampaignGenerationJobs",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_RequestedByUserId",
                table: "CampaignGenerationJobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_Status_NextAttemptAtUtc_LeaseExpires~",
                table: "CampaignGenerationJobs",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_VideoNarrationId_PropertyId_Organiza~",
                table: "CampaignGenerationJobs",
                columns: new[] { "VideoNarrationId", "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignDeliverables");

            migrationBuilder.DropTable(
                name: "CampaignGenerationJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_VideoNarrations_Id_PropertyId_OrganizationId",
                table: "VideoNarrations");
        }
    }
}
#pragma warning restore CA1861
