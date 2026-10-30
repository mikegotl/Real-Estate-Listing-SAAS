using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateBillingPlansAndTesterLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingInterval",
                table: "OrganizationBillingAccounts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubscriptionPeriodEndUtc",
                table: "OrganizationBillingAccounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubscriptionPeriodStartUtc",
                table: "OrganizationBillingAccounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TesterMonthlyCampaignLimit",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            // Plans are stored by name: Free is now Trial and Starter is now Basic.
            migrationBuilder.Sql("""
                UPDATE "OrganizationBillingAccounts" SET "Plan" = 'Trial' WHERE "Plan" = 'Free';
                UPDATE "OrganizationBillingAccounts" SET "Plan" = 'Basic' WHERE "Plan" = 'Starter';
                UPDATE "OrganizationBillingAccounts"
                SET "SubscriptionPeriodStartUtc" = "CurrentPeriodStartUtc",
                    "SubscriptionPeriodEndUtc" = "CurrentPeriodEndUtc"
                WHERE "StripeSubscriptionId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "OrganizationBillingAccounts" SET "Plan" = 'Free' WHERE "Plan" = 'Trial';
                UPDATE "OrganizationBillingAccounts" SET "Plan" = 'Starter' WHERE "Plan" = 'Basic';
                """);

            migrationBuilder.DropColumn(
                name: "BillingInterval",
                table: "OrganizationBillingAccounts");

            migrationBuilder.DropColumn(
                name: "SubscriptionPeriodEndUtc",
                table: "OrganizationBillingAccounts");

            migrationBuilder.DropColumn(
                name: "SubscriptionPeriodStartUtc",
                table: "OrganizationBillingAccounts");

            migrationBuilder.DropColumn(
                name: "TesterMonthlyCampaignLimit",
                table: "AspNetUsers");
        }
    }
}
