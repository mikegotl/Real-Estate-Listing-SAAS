using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncreaseFreeCampaignAllowanceToTen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "OrganizationBillingAccounts"
                SET "MonthlyCampaignAllowance" = 10,
                    "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "Plan" = 'Free'
                  AND "StripeSubscriptionId" IS NULL
                  AND "CurrentPeriodEndUtc" > CURRENT_TIMESTAMP
                  AND "MonthlyCampaignAllowance" < 10;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous per-account allowance cannot be reconstructed safely.
        }
    }
}
