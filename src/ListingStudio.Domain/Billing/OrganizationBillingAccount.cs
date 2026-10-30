namespace ListingStudio.Domain.Billing;

using ListingStudio.Domain.Organizations;

/// <summary>
/// The workspace's plan. Stored by name, so renaming a value needs a data migration
/// (Free became Trial and Starter became Basic in RenameBillingPlans).
/// </summary>
public enum SubscriptionPlan
{
    Trial,
    Basic,
    Professional,
    Enterprise,
}

/// <summary>Which paid AI features each plan includes.</summary>
public static class SubscriptionPlanFeatures
{
    /// <summary>The AI drone intro and the AI agent presenter come with Professional and Enterprise.</summary>
    public static bool IncludesPremiumAi(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Professional or SubscriptionPlan.Enterprise;

    public static bool IsPaid(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Basic or SubscriptionPlan.Professional or SubscriptionPlan.Enterprise;

    public static string DisplayName(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Trial => "Trial",
        SubscriptionPlan.Basic => "Basic",
        SubscriptionPlan.Professional => "Professional",
        SubscriptionPlan.Enterprise => "Enterprise",
        _ => plan.ToString(),
    };
}

/// <summary>How often a paid plan is billed. Campaign limits reset every month either way.</summary>
public enum BillingInterval
{
    Monthly,
    Annual,
}

public enum BillingSubscriptionStatus
{
    None,
    Incomplete,
    IncompleteExpired,
    Trialing,
    Active,
    PastDue,
    Canceled,
    Unpaid,
    Paused,
}

public sealed class OrganizationBillingAccount
{
    private OrganizationBillingAccount()
    {
    }

    private OrganizationBillingAccount(
        Guid organizationId,
        DateTimeOffset now,
        int freeTrialCampaignAllowance,
        int freeTrialDays)
    {
        OrganizationId = organizationId;
        Plan = SubscriptionPlan.Trial;
        SubscriptionStatus = BillingSubscriptionStatus.None;
        CurrentPeriodStartUtc = now;
        CurrentPeriodEndUtc = now.AddDays(freeTrialDays);
        MonthlyCampaignAllowance = freeTrialCampaignAllowance;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid OrganizationId { get; private set; }

    public string? StripeCustomerId { get; private set; }

    public string? StripeSubscriptionId { get; private set; }

    public SubscriptionPlan Plan { get; private set; }

    public BillingInterval BillingInterval { get; private set; }

    /// <summary>The paid period Stripe bills for (a month or a year); null before the first subscription.</summary>
    public DateTimeOffset? SubscriptionPeriodStartUtc { get; private set; }

    public DateTimeOffset? SubscriptionPeriodEndUtc { get; private set; }

    public BillingSubscriptionStatus SubscriptionStatus { get; private set; }

    /// <summary>
    /// The current campaign-usage window: the trial, or one month of the paid period. An annual subscription moves
    /// through twelve monthly windows, each with a fresh allowance.
    /// </summary>
    public DateTimeOffset CurrentPeriodStartUtc { get; private set; }

    public DateTimeOffset CurrentPeriodEndUtc { get; private set; }

    public int MonthlyCampaignAllowance { get; private set; }

    public int CampaignUsage { get; private set; }

    public int AdditionalCampaignUsage { get; private set; }

    public DateTimeOffset? LastStripeEventCreatedUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public static OrganizationBillingAccount Create(
        Guid organizationId,
        DateTimeOffset now,
        int freeTrialCampaignAllowance = 2,
        int freeTrialDays = 14)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(freeTrialCampaignAllowance);
        if (freeTrialDays is < 1 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(freeTrialDays));
        }

        return new OrganizationBillingAccount(
            organizationId,
            now,
            freeTrialCampaignAllowance,
            freeTrialDays);
    }

    public void LinkCustomer(string stripeCustomerId, DateTimeOffset now)
    {
        var normalized = RequiredStripeId(stripeCustomerId, "cus_", nameof(stripeCustomerId));
        if (StripeCustomerId is not null && !string.Equals(StripeCustomerId, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The billing account is already linked to a different Stripe customer.");
        }

        StripeCustomerId = normalized;
        UpdatedAtUtc = now;
    }

    public void LinkSubscription(string stripeSubscriptionId, DateTimeOffset now)
    {
        StripeSubscriptionId = RequiredStripeId(stripeSubscriptionId, "sub_", nameof(stripeSubscriptionId));
        UpdatedAtUtc = now;
    }

    public bool ApplySubscription(
        string stripeCustomerId,
        string stripeSubscriptionId,
        SubscriptionPlan plan,
        BillingSubscriptionStatus status,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        int monthlyCampaignAllowance,
        DateTimeOffset stripeEventCreatedUtc,
        DateTimeOffset now,
        BillingInterval interval = BillingInterval.Monthly)
    {
        if (stripeEventCreatedUtc < LastStripeEventCreatedUtc)
        {
            return false;
        }

        if (periodEndUtc <= periodStartUtc)
        {
            throw new ArgumentException("The subscription period end must follow its start.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(monthlyCampaignAllowance);
        LinkCustomer(stripeCustomerId, now);
        LinkSubscription(stripeSubscriptionId, now);

        var window = UsageWindow(periodStartUtc, periodEndUtc, now);
        if (SubscriptionPeriodStartUtc != periodStartUtc
            || SubscriptionPeriodEndUtc != periodEndUtc
            || CurrentPeriodStartUtc != window.Start)
        {
            CampaignUsage = 0;
            AdditionalCampaignUsage = 0;
        }

        Plan = plan;
        BillingInterval = interval;
        SubscriptionStatus = status;
        SubscriptionPeriodStartUtc = periodStartUtc;
        SubscriptionPeriodEndUtc = periodEndUtc;
        CurrentPeriodStartUtc = window.Start;
        CurrentPeriodEndUtc = window.End;
        MonthlyCampaignAllowance = monthlyCampaignAllowance;
        LastStripeEventCreatedUtc = stripeEventCreatedUtc;
        UpdatedAtUtc = now;
        return true;
    }

    /// <summary>
    /// Moves an annual subscription into the month that contains <paramref name="now"/> and starts that month's
    /// allowance fresh. Returns false when the current window is still running or the paid period has ended.
    /// </summary>
    public bool AdvanceUsageWindow(DateTimeOffset now)
    {
        if (SubscriptionPeriodStartUtc is not { } start
            || SubscriptionPeriodEndUtc is not { } end
            || now < CurrentPeriodEndUtc
            || CurrentPeriodEndUtc >= end)
        {
            return false;
        }

        var window = UsageWindow(start, end, now);
        CurrentPeriodStartUtc = window.Start;
        CurrentPeriodEndUtc = window.End;
        CampaignUsage = 0;
        AdditionalCampaignUsage = 0;
        UpdatedAtUtc = now;
        return true;
    }

    /// <summary>The paid period ends when Stripe's billed period does, not when the monthly usage window does.</summary>
    public DateTimeOffset PaidThroughUtc => SubscriptionPeriodEndUtc ?? CurrentPeriodEndUtc;

    // Months count from the paid period's start, so a subscription that began on the 31st resets on the last day of
    // shorter months, as AddMonths does.
    private static (DateTimeOffset Start, DateTimeOffset End) UsageWindow(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset at)
    {
        var months = 0;
        while (periodStart.AddMonths(months + 1) <= at && periodStart.AddMonths(months + 1) < periodEnd)
        {
            months++;
        }

        var windowEnd = periodStart.AddMonths(months + 1);
        return (periodStart.AddMonths(months), windowEnd < periodEnd ? windowEnd : periodEnd);
    }

    public void RecordCampaignUsage(DateTimeOffset now)
    {
        CampaignUsage++;
        AdditionalCampaignUsage = Math.Max(0, CampaignUsage - MonthlyCampaignAllowance);
        UpdatedAtUtc = now;
    }

    public bool ExpireFreeTrial(DateTimeOffset now)
    {
        if (Plan != SubscriptionPlan.Trial || now < CurrentPeriodEndUtc || MonthlyCampaignAllowance == 0)
        {
            return false;
        }

        MonthlyCampaignAllowance = 0;
        UpdatedAtUtc = now;
        return true;
    }

    private static string RequiredStripeId(string value, string prefix, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 255 || !normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A valid Stripe {prefix} identifier is required.", parameterName);
        }

        return normalized;
    }
}
