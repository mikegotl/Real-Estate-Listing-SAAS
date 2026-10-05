namespace ListingStudio.Application.Billing;

public interface IBillingProviderGateway
{
    Task<string> CreateCustomerAsync(
        BillingProviderCustomerRequest request,
        CancellationToken cancellationToken = default);

    Task<BillingRedirect> CreateCheckoutSessionAsync(
        BillingProviderCheckoutRequest request,
        CancellationToken cancellationToken = default);

    Task<BillingRedirect> CreatePortalSessionAsync(
        BillingProviderPortalRequest request,
        CancellationToken cancellationToken = default);

    object ParseAndVerifyWebhook(string payload, string signatureHeader);
}
