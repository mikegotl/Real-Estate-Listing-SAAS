namespace ListingStudio.UnitTests;

using ListingStudio.Domain.Billing;
using Xunit;

public sealed class OrganizationBillingAccountTests
{
    [Fact]
    public void NewAccountReceivesTenCampaignThirtyDayTrial()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var account = OrganizationBillingAccount.Create(Guid.NewGuid(), now);

        Assert.Equal(SubscriptionPlan.Free, account.Plan);
        Assert.Equal(10, account.MonthlyCampaignAllowance);
        Assert.Equal(now, account.CurrentPeriodStartUtc);
        Assert.Equal(now.AddDays(30), account.CurrentPeriodEndUtc);
        Assert.False(account.ExpireFreeTrial(now.AddDays(29)));
        Assert.True(account.ExpireFreeTrial(now.AddDays(30)));
        Assert.Equal(0, account.MonthlyCampaignAllowance);
        Assert.False(account.ExpireFreeTrial(now.AddDays(60)));
    }

    [Fact]
    public void TracksIncludedAndAdditionalCampaignUsageAndResetsForANewPeriod()
    {
        var organizationId = Guid.NewGuid();
        var periodStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var account = OrganizationBillingAccount.Create(organizationId, periodStart);
        Assert.True(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Starter,
            BillingSubscriptionStatus.Active,
            periodStart,
            periodStart.AddMonths(1),
            2,
            periodStart,
            periodStart));

        account.RecordCampaignUsage(periodStart.AddDays(1));
        account.RecordCampaignUsage(periodStart.AddDays(2));
        account.RecordCampaignUsage(periodStart.AddDays(3));

        Assert.Equal(3, account.CampaignUsage);
        Assert.Equal(1, account.AdditionalCampaignUsage);

        Assert.True(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Starter,
            BillingSubscriptionStatus.Active,
            periodStart.AddMonths(1),
            periodStart.AddMonths(2),
            2,
            periodStart.AddMonths(1),
            periodStart.AddMonths(1)));
        Assert.Equal(0, account.CampaignUsage);
        Assert.Equal(0, account.AdditionalCampaignUsage);
    }

    [Fact]
    public void OlderStripeEventCannotOverwriteNewerSubscriptionState()
    {
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var account = OrganizationBillingAccount.Create(Guid.NewGuid(), now);
        Assert.True(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Professional,
            BillingSubscriptionStatus.Active,
            now,
            now.AddMonths(1),
            12,
            now,
            now));

        Assert.False(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Starter,
            BillingSubscriptionStatus.Canceled,
            now,
            now.AddMonths(1),
            4,
            now.AddSeconds(-1),
            now.AddSeconds(1)));
        Assert.Equal(SubscriptionPlan.Professional, account.Plan);
        Assert.Equal(BillingSubscriptionStatus.Active, account.SubscriptionStatus);
    }
}
