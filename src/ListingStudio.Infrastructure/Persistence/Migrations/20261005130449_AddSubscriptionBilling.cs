using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_CampaignGenerationJobs_Id_OrganizationId",
                table: "CampaignGenerationJobs",
                columns: new[] { "Id", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "BillingWebhookReceipts",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EventCreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingWebhookReceipts", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "CampaignUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsAdditionalUsage = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignUsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignUsageRecords_CampaignGenerationJobs_CampaignGenerat~",
                        columns: x => new { x.CampaignGenerationJobId, x.OrganizationId },
                        principalTable: "CampaignGenerationJobs",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignUsageRecords_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationBillingAccounts",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubscriptionStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentPeriodStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MonthlyCampaignAllowance = table.Column<int>(type: "integer", nullable: false),
                    CampaignUsage = table.Column<int>(type: "integer", nullable: false),
                    AdditionalCampaignUsage = table.Column<int>(type: "integer", nullable: false),
                    LastStripeEventCreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationBillingAccounts", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_OrganizationBillingAccounts_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingWebhookReceipts_ProcessedAtUtc",
                table: "BillingWebhookReceipts",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageRecords_CampaignGenerationJobId",
                table: "CampaignUsageRecords",
                column: "CampaignGenerationJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageRecords_CampaignGenerationJobId_OrganizationId",
                table: "CampaignUsageRecords",
                columns: new[] { "CampaignGenerationJobId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageRecords_OrganizationId_PeriodStartUtc",
                table: "CampaignUsageRecords",
                columns: new[] { "OrganizationId", "PeriodStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationBillingAccounts_StripeCustomerId",
                table: "OrganizationBillingAccounts",
                column: "StripeCustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationBillingAccounts_StripeSubscriptionId",
                table: "OrganizationBillingAccounts",
                column: "StripeSubscriptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingWebhookReceipts");

            migrationBuilder.DropTable(
                name: "CampaignUsageRecords");

            migrationBuilder.DropTable(
                name: "OrganizationBillingAccounts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CampaignGenerationJobs_Id_OrganizationId",
                table: "CampaignGenerationJobs");
        }
    }
}
