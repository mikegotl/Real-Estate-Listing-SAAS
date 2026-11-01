using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: posts of vertical library videos (CampaignDistributionRecords), append-only metric snapshots
    /// (CampaignMetricSnapshots, protected by a trigger that rejects UPDATE and DELETE) and draft rule evaluation reports.
    /// Nothing existing is rewritten.
    /// </summary>
    public partial class AddVerticalPerformanceMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignDistributionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignVideoVariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignDeliverableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PostUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignDistributionRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignDistributionRecords_CampaignDeliverables_CampaignDe~",
                        column: x => x.CampaignDeliverableId,
                        principalTable: "CampaignDeliverables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignDistributionRecords_CampaignVideoVariants_CampaignV~",
                        column: x => x.CampaignVideoVariantId,
                        principalTable: "CampaignVideoVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerticalRuleEvaluationReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    BaseRuleVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CalculationVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    VideoCount = table.Column<int>(type: "integer", nullable: false),
                    TotalViews = table.Column<long>(type: "bigint", nullable: false),
                    MeetsMinimumSample = table.Column<bool>(type: "boolean", nullable: false),
                    SegmentsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PromotedRuleVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PromotedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    PromotedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerticalRuleEvaluationReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignMetricSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DistributionRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: false),
                    Likes = table.Column<long>(type: "bigint", nullable: false),
                    Comments = table.Column<long>(type: "bigint", nullable: false),
                    Shares = table.Column<long>(type: "bigint", nullable: false),
                    Saves = table.Column<long>(type: "bigint", nullable: false),
                    AverageWatchSeconds = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    CompletionRate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    Clicks = table.Column<long>(type: "bigint", nullable: false),
                    Leads = table.Column<int>(type: "integer", nullable: false),
                    BookedShowings = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EnteredByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignMetricSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignMetricSnapshots_CampaignDistributionRecords_Distrib~",
                        column: x => x.DistributionRecordId,
                        principalTable: "CampaignDistributionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDistributionRecords_CampaignDeliverableId",
                table: "CampaignDistributionRecords",
                column: "CampaignDeliverableId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDistributionRecords_CampaignVideoVariantId",
                table: "CampaignDistributionRecords",
                column: "CampaignVideoVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDistributionRecords_OrganizationId_CampaignGenerati~",
                table: "CampaignDistributionRecords",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDistributionRecords_OrganizationId_CampaignVideoVar~",
                table: "CampaignDistributionRecords",
                columns: new[] { "OrganizationId", "CampaignVideoVariantId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDistributionRecords_OrganizationId_PostUrl",
                table: "CampaignDistributionRecords",
                columns: new[] { "OrganizationId", "PostUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignMetricSnapshots_DistributionRecordId_CapturedAtUtc",
                table: "CampaignMetricSnapshots",
                columns: new[] { "DistributionRecordId", "CapturedAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignMetricSnapshots_OrganizationId",
                table: "CampaignMetricSnapshots",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_VerticalRuleEvaluationReports_OrganizationId_CreatedAtUtc",
                table: "VerticalRuleEvaluationReports",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION reject_campaign_metric_snapshot_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Campaign metric snapshots are append-only';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER campaign_metric_snapshots_append_only
                BEFORE UPDATE OR DELETE ON "CampaignMetricSnapshots"
                FOR EACH ROW EXECUTE FUNCTION reject_campaign_metric_snapshot_mutation();
                """);
        }

        /// <summary>Development only: drops the measurement tables and every recorded number with them.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS campaign_metric_snapshots_append_only ON "CampaignMetricSnapshots";
                DROP FUNCTION IF EXISTS reject_campaign_metric_snapshot_mutation();
                """);

            migrationBuilder.DropTable(
                name: "CampaignMetricSnapshots");

            migrationBuilder.DropTable(
                name: "VerticalRuleEvaluationReports");

            migrationBuilder.DropTable(
                name: "CampaignDistributionRecords");
        }
    }
}
#pragma warning restore CA1861
