namespace ListingStudio.Application.Billing;

using ListingStudio.Domain.Billing;

public interface IBillingService
{
    Task<BillingOverview> GetOverviewAsync(string userId, CancellationToken cancellationToken = default);

    Task<BillingRedirect> CreateCheckoutSessionAsync(
        string userId,
        SubscriptionPlan plan,
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
    Task EnsureCampaignCapacityAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task RecordCompletedCampaignAsync(
        Guid organizationId,
        Guid campaignGenerationJobId,
        CancellationToken cancellationToken = default);
}

public sealed class CampaignAllowanceExceededException(string message) : InvalidOperationException(message);
