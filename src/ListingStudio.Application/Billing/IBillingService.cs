namespace ListingStudio.Application.Billing;

using ListingStudio.Domain.Billing;

public interface IBillingService
{
    Task<BillingOverview> GetOverviewAsync(string userId, CancellationToken cancellationToken = default);

    Task<BillingRedirect> CreateCheckoutSessionAsync(
        string userId,
        SubscriptionPlan plan,
        BillingInterval interval,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    Task<BillingRedirect> CreatePortalSessionAsync(
        string userId,
        string returnUrl,
        CancellationToken cancellationToken = default);

    Task<BillingWebhookOutcome> ProcessWebhookAsync(
        string payload,
        string signatureHeader,
        CancellationToken cancellationToken = default);
}

public interface ICampaignBillingGate
{
    /// <summary>
    /// Throws <see cref="CampaignAllowanceExceededException"/> when the workspace cannot start another campaign.
    /// Platform admins are never limited. Testers skip the workspace plan but have their own monthly limit, and the
    /// platform admins are notified in the app and by email when a tester starts the last one.
    /// </summary>
    Task EnsureCampaignCapacityAsync(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Counts a finished campaign against the allowance, unless a tester or platform admin requested it.</summary>

    Task RecordCompletedCampaignAsync(
        Guid organizationId,
        Guid campaignGenerationJobId,
        CancellationToken cancellationToken = default);
}

public sealed class CampaignAllowanceExceededException(string message) : InvalidOperationException(message);

/// <summary>Which plan-gated features a user can use in their workspace right now.</summary>
public interface IPlanFeatureGate
{
    Task<PlanFeatureAccess> GetAccessAsync(string userId, CancellationToken cancellationToken = default);

    Task<PlanFeatureAccess> GetAccessAsync(
        Guid organizationId,
        string userId,
        CancellationToken cancellationToken = default);
}

/// <param name="Plan">The workspace's plan.</param>
/// <param name="IncludesPremiumAi">
/// Whether the AI drone intro and the AI agent presenter are available: on an active Professional or Enterprise plan,
/// or always for testers and platform admins.
/// </param>
public sealed record PlanFeatureAccess(SubscriptionPlan Plan, bool IncludesPremiumAi)
{
    public const string PremiumAiUpgradeMessage =
        "The AI drone intro and the AI agent presenter are included with the Professional and Enterprise plans. Upgrade on Billing to use them.";
}

public sealed class PlanFeatureUnavailableException(string message) : InvalidOperationException(message);
