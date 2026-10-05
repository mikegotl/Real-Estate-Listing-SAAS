namespace ListingStudio.Application.Billing;

using ListingStudio.Domain.Billing;

public sealed record BillingOverview(
    Guid OrganizationId,
    string OrganizationName,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    SubscriptionPlan Plan,
    BillingSubscriptionStatus SubscriptionStatus,
    DateTimeOffset CurrentPeriodStartUtc,
    DateTimeOffset CurrentPeriodEndUtc,
    int MonthlyCampaignAllowance,
    int CampaignUsage,
    int AdditionalCampaignUsage);

public sealed record BillingRedirect(string Url);

public sealed record BillingProviderCustomerRequest(
    Guid OrganizationId,
    string OrganizationName,
    string OwnerEmail,
    string IdempotencyKey);

public sealed record BillingProviderCheckoutRequest(
    Guid OrganizationId,
    string StripeCustomerId,
    SubscriptionPlan Plan,
    string PriceId,
    string SuccessUrl,
    string CancelUrl,
    string IdempotencyKey);

public sealed record BillingProviderPortalRequest(
    string StripeCustomerId,
    string ReturnUrl,
    string IdempotencyKey);

public sealed record BillingProviderSubscriptionEvent(
    string EventId,
    string EventType,
    DateTimeOffset EventCreatedUtc,
    string PayloadSha256,
    string StripeCustomerId,
    string StripeSubscriptionId,
    string PriceId,
    string Status,
    DateTimeOffset CurrentPeriodStartUtc,
    DateTimeOffset CurrentPeriodEndUtc);

public sealed record BillingProviderCheckoutCompletedEvent(
    string EventId,
    string EventType,
    DateTimeOffset EventCreatedUtc,
    string PayloadSha256,
    Guid OrganizationId,
    string StripeCustomerId,
    string StripeSubscriptionId);

public sealed record BillingProviderIgnoredEvent(
    string EventId,
    string EventType,
    DateTimeOffset EventCreatedUtc,
    string PayloadSha256);

public enum BillingWebhookOutcome
{
    Processed,
    AlreadyProcessed,
    Ignored,
}
