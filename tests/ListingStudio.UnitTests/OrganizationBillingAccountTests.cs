namespace ListingStudio.UnitTests;

using ListingStudio.Domain.Billing;
using Xunit;

public sealed class OrganizationBillingAccountTests
{
    [Fact]
    public void NewAccountReceivesTwoCampaignFourteenDayTrial()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var account = OrganizationBillingAccount.Create(Guid.NewGuid(), now);

        Assert.Equal(SubscriptionPlan.Trial, account.Plan);
        Assert.Equal(2, account.MonthlyCampaignAllowance);
        Assert.Equal(now, account.CurrentPeriodStartUtc);
        Assert.Equal(now.AddDays(14), account.CurrentPeriodEndUtc);
        Assert.False(account.ExpireFreeTrial(now.AddDays(13)));
        Assert.True(account.ExpireFreeTrial(now.AddDays(14)));
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
            SubscriptionPlan.Basic,
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
            SubscriptionPlan.Basic,
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
            SubscriptionPlan.Basic,
            BillingSubscriptionStatus.Canceled,
            now,
            now.AddMonths(1),
            4,
            now.AddSeconds(-1),
            now.AddSeconds(1)));
        Assert.Equal(SubscriptionPlan.Professional, account.Plan);
        Assert.Equal(BillingSubscriptionStatus.Active, account.SubscriptionStatus);
    }

    [Fact]
    public void AnnualSubscriptionResetsTheCampaignAllowanceEveryMonth()
    {
        var yearStart = new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);
        var account = OrganizationBillingAccount.Create(Guid.NewGuid(), yearStart);
        Assert.True(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Enterprise,
            BillingSubscriptionStatus.Active,
            yearStart,
            yearStart.AddYears(1),
            30,
            yearStart,
            yearStart,
            BillingInterval.Annual));

        Assert.Equal(BillingInterval.Annual, account.BillingInterval);
        Assert.Equal(yearStart, account.CurrentPeriodStartUtc);
        Assert.Equal(yearStart.AddMonths(1), account.CurrentPeriodEndUtc);
        Assert.Equal(yearStart.AddYears(1), account.PaidThroughUtc);
        account.RecordCampaignUsage(yearStart.AddDays(3));
        Assert.False(account.AdvanceUsageWindow(yearStart.AddDays(20)));
        Assert.Equal(1, account.CampaignUsage);

        // Months count from the anchor: Jan 31 -> Feb 28 -> Mar 31.
        var march = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.True(account.AdvanceUsageWindow(march));
        Assert.Equal(0, account.CampaignUsage);
        Assert.Equal(yearStart.AddMonths(1), account.CurrentPeriodStartUtc);
        Assert.Equal(yearStart.AddMonths(2), account.CurrentPeriodEndUtc);

        // A mid-year webhook for the same paid year keeps the month's usage.
        account.RecordCampaignUsage(march);
        Assert.True(account.ApplySubscription(
            "cus_test",
            "sub_test",
            SubscriptionPlan.Enterprise,
            BillingSubscriptionStatus.Active,
            yearStart,
            yearStart.AddYears(1),
            30,
            march,
            march,
            BillingInterval.Annual));
        Assert.Equal(1, account.CampaignUsage);

        // The last window ends with the paid year and never runs past it.
        var lastMonth = yearStart.AddYears(1).AddDays(-2);
        Assert.True(account.AdvanceUsageWindow(lastMonth));
        Assert.Equal(yearStart.AddYears(1), account.CurrentPeriodEndUtc);
        Assert.False(account.AdvanceUsageWindow(yearStart.AddYears(1).AddDays(5)));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Trial, false)]
    [InlineData(SubscriptionPlan.Basic, false)]
    [InlineData(SubscriptionPlan.Professional, true)]
    [InlineData(SubscriptionPlan.Enterprise, true)]
    public void OnlyProfessionalAndEnterpriseIncludeTheDroneIntroAndPresenter(SubscriptionPlan plan, bool included)
    {
        Assert.Equal(included, SubscriptionPlanFeatures.IncludesPremiumAi(plan));
        Assert.Equal(plan != SubscriptionPlan.Trial, SubscriptionPlanFeatures.IsPaid(plan));
    }
}
