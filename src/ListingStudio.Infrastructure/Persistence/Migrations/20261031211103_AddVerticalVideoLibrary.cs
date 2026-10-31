using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerticalVideoLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CampaignDeliverables_OrganizationId_CampaignGenerationJobId~",
                table: "CampaignDeliverables");

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignVideoVariantId",
                table: "CampaignDeliverables",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CampaignVideoLibraries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Selection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RecommendationSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignVideoLibraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignVideoLibraries_CampaignGenerationJobs_CampaignGener~",
                        columns: x => new { x.CampaignGenerationJobId, x.PropertyId, x.OrganizationId },
                        principalTable: "CampaignGenerationJobs",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsGloballyEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabledForAllOrganizations = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "CampaignVideoVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignVideoLibraryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsEligible = table.Column<bool>(type: "boolean", nullable: false),
                    HookText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HookGroundingKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PrimaryPropertyMediaId = table.Column<Guid>(type: "uuid", nullable: true),
                    VoiceoverMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CallToAction = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RecipeJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecipeVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CaptionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignVideoVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignVideoVariants_CampaignVideoLibraries_CampaignVideoL~",
                        column: x => x.CampaignVideoLibraryId,
                        principalTable: "CampaignVideoLibraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlagOrganizations",
                columns: table => new
                {
                    FeatureFlagKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlagOrganizations", x => new { x.FeatureFlagKey, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_FeatureFlagOrganizations_FeatureFlags_FeatureFlagKey",
                        column: x => x.FeatureFlagKey,
                        principalTable: "FeatureFlags",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FeatureFlagOrganizations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_CampaignVideoVariantId",
                table: "CampaignDeliverables",
                column: "CampaignVideoVariantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_OrganizationId_CampaignGenerationJobId~",
                table: "CampaignDeliverables",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId", "Kind" },
                unique: true,
                filter: "\"CampaignVideoVariantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoLibraries_CampaignGenerationJobId",
                table: "CampaignVideoLibraries",
                column: "CampaignGenerationJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoLibraries_CampaignGenerationJobId_PropertyId_O~",
                table: "CampaignVideoLibraries",
                columns: new[] { "CampaignGenerationJobId", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoLibraries_OrganizationId_PropertyId_CreatedAtU~",
                table: "CampaignVideoLibraries",
                columns: new[] { "OrganizationId", "PropertyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoLibraries_Status_UpdatedAtUtc",
                table: "CampaignVideoLibraries",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoVariants_CampaignVideoLibraryId_Format_Sequence",
                table: "CampaignVideoVariants",
                columns: new[] { "CampaignVideoLibraryId", "Format", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoVariants_OrganizationId_CampaignVideoLibraryId",
                table: "CampaignVideoVariants",
                columns: new[] { "OrganizationId", "CampaignVideoLibraryId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoVariants_OrganizationId_Format_Status",
                table: "CampaignVideoVariants",
                columns: new[] { "OrganizationId", "Format", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlagOrganizations_OrganizationId",
                table: "FeatureFlagOrganizations",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CampaignDeliverables_CampaignVideoVariants_CampaignVideoVar~",
                table: "CampaignDeliverables",
                column: "CampaignVideoVariantId",
                principalTable: "CampaignVideoVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill: every finished campaign's TikTok, Reel and Short outputs become one legacy library with a
            // LegacySingle video each. Identities are reused (library = campaign, video = deliverable) so the
            // backfill is deterministic and safe to run again. Campaigns still running keep the legacy path only.
            migrationBuilder.Sql(BackfillSql);
        }

        public const string BackfillSql = """
            INSERT INTO "CampaignVideoLibraries"
                ("Id", "OrganizationId", "PropertyId", "CampaignGenerationJobId", "Selection", "Status",
                 "RuleVersion", "RecommendationSnapshotJson", "CreatedAtUtc", "UpdatedAtUtc", "CompletedAtUtc")
            SELECT j."Id", j."OrganizationId", j."PropertyId", j."Id", 'LegacySingle',
                   CASE
                       WHEN bool_and(d."Status" = 'Rendered') THEN 'Completed'
                       WHEN bool_or(d."Status" = 'Rendered') THEN 'PartiallyCompleted'
                       WHEN j."Status" = 'Cancelled' THEN 'Cancelled'
                       ELSE 'Failed'
                   END,
                   NULL, NULL, j."CreatedAtUtc", j."UpdatedAtUtc", COALESCE(j."CompletedAtUtc", j."UpdatedAtUtc")
            FROM "CampaignGenerationJobs" j
            JOIN "CampaignDeliverables" d
              ON d."CampaignGenerationJobId" = j."Id" AND d."OrganizationId" = j."OrganizationId"
            WHERE d."Kind" IN ('TikTok', 'InstagramReel', 'YouTubeShort')
              AND j."Status" IN ('Completed', 'Failed', 'Cancelled')
              AND NOT EXISTS (SELECT 1 FROM "CampaignVideoLibraries" l WHERE l."CampaignGenerationJobId" = j."Id")
            GROUP BY j."Id", j."OrganizationId", j."PropertyId", j."Status", j."CreatedAtUtc", j."UpdatedAtUtc",
                     j."CompletedAtUtc";

            INSERT INTO "CampaignVideoVariants"
                ("Id", "OrganizationId", "CampaignVideoLibraryId", "Format", "Purpose", "Sequence", "DurationSeconds",
                 "IsEligible", "HookText", "HookGroundingKey", "PrimaryPropertyMediaId", "VoiceoverMode", "CallToAction",
                 "RecipeJson", "RecipeVersion", "CaptionsJson", "Status", "Error", "CreatedAtUtc", "UpdatedAtUtc",
                 "StartedAtUtc", "FinishedAtUtc")
            SELECT d."Id", d."OrganizationId", l."Id", 'LegacySingle', 'Reach',
                   row_number() OVER (
                       PARTITION BY l."Id"
                       ORDER BY CASE d."Kind" WHEN 'TikTok' THEN 1 WHEN 'InstagramReel' THEN 2 ELSE 3 END),
                   d."RequestedDuration", TRUE, NULL, NULL, NULL, j."ShortFormVoiceover", NULL,
                   jsonb_build_object('format', 'LegacySingle', 'deliverableKind', d."Kind", 'backfilled', TRUE),
                   'legacy-single-v1', NULL,
                   CASE
                       WHEN d."Status" = 'Rendered' THEN 'Rendered'
                       WHEN j."Status" = 'Cancelled' THEN 'Cancelled'
                       ELSE 'Failed'
                   END,
                   CASE WHEN d."Status" = 'Rendered' OR j."Status" = 'Cancelled' THEN NULL
                        ELSE 'Not rendered before the vertical video library existed.' END,
                   d."CreatedAtUtc", COALESCE(d."RenderedAtUtc", d."CreatedAtUtc"),
                   d."CreatedAtUtc", COALESCE(d."RenderedAtUtc", j."UpdatedAtUtc")
            FROM "CampaignDeliverables" d
            JOIN "CampaignGenerationJobs" j ON j."Id" = d."CampaignGenerationJobId"
            JOIN "CampaignVideoLibraries" l ON l."CampaignGenerationJobId" = j."Id" AND l."Selection" = 'LegacySingle'
            WHERE d."Kind" IN ('TikTok', 'InstagramReel', 'YouTubeShort')
              AND d."CampaignVideoVariantId" IS NULL
              AND NOT EXISTS (SELECT 1 FROM "CampaignVideoVariants" v WHERE v."Id" = d."Id");

            UPDATE "CampaignDeliverables" d
            SET "CampaignVideoVariantId" = d."Id"
            WHERE d."CampaignVideoVariantId" IS NULL
              AND EXISTS (SELECT 1 FROM "CampaignVideoVariants" v WHERE v."Id" = d."Id");
            """;

        /// <summary>
        /// Development only. Production rollback is the VerticalVideoLibraryV1 switch: turn it off and leave these
        /// tables and links in place. Removing them later needs the owner's approval and a retention plan.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CampaignDeliverables_CampaignVideoVariants_CampaignVideoVar~",
                table: "CampaignDeliverables");

            migrationBuilder.DropTable(
                name: "CampaignVideoVariants");

            migrationBuilder.DropTable(
                name: "FeatureFlagOrganizations");

            migrationBuilder.DropTable(
                name: "CampaignVideoLibraries");

            migrationBuilder.DropTable(
                name: "FeatureFlags");

            migrationBuilder.DropIndex(
                name: "IX_CampaignDeliverables_CampaignVideoVariantId",
                table: "CampaignDeliverables");

            migrationBuilder.DropIndex(
                name: "IX_CampaignDeliverables_OrganizationId_CampaignGenerationJobId~",
                table: "CampaignDeliverables");

            migrationBuilder.DropColumn(
                name: "CampaignVideoVariantId",
                table: "CampaignDeliverables");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_OrganizationId_CampaignGenerationJobId~",
                table: "CampaignDeliverables",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId", "Kind" },
                unique: true);
        }
    }
}
#pragma warning restore CA1861
