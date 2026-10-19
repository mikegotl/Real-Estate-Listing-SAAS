using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantExistingAccountsFreeCampaignTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "OrganizationBillingAccounts"
                SET "CurrentPeriodStartUtc" = CURRENT_TIMESTAMP,
                    "CurrentPeriodEndUtc" = CURRENT_TIMESTAMP + INTERVAL '30 days',
                    "MonthlyCampaignAllowance" = 1,
                    "CampaignUsage" = 0,
                    "AdditionalCampaignUsage" = 0,
                    "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "Plan" = 'Free'
                  AND "StripeSubscriptionId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The prior per-account period and usage cannot be reconstructed safely.
        }
    }
}
