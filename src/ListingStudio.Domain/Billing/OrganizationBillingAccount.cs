namespace ListingStudio.Domain.Billing;

using ListingStudio.Domain.Organizations;

public enum SubscriptionPlan
{
    Free,
    Starter,
    Professional,
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
        Plan = SubscriptionPlan.Free;
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

    public BillingSubscriptionStatus SubscriptionStatus { get; private set; }

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
        int freeTrialCampaignAllowance = 10,
        int freeTrialDays = 30)
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
        DateTimeOffset now)
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

        if (CurrentPeriodStartUtc != periodStartUtc || CurrentPeriodEndUtc != periodEndUtc)
        {
            CampaignUsage = 0;
            AdditionalCampaignUsage = 0;
        }

        Plan = plan;
        SubscriptionStatus = status;
        CurrentPeriodStartUtc = periodStartUtc;
        CurrentPeriodEndUtc = periodEndUtc;
        MonthlyCampaignAllowance = monthlyCampaignAllowance;
        LastStripeEventCreatedUtc = stripeEventCreatedUtc;
        UpdatedAtUtc = now;
        return true;
    }

    public void RecordCampaignUsage(DateTimeOffset now)
    {
        CampaignUsage++;
        AdditionalCampaignUsage = Math.Max(0, CampaignUsage - MonthlyCampaignAllowance);
        UpdatedAtUtc = now;
    }

    public bool ExpireFreeTrial(DateTimeOffset now)
    {
        if (Plan != SubscriptionPlan.Free || now < CurrentPeriodEndUtc || MonthlyCampaignAllowance == 0)
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
