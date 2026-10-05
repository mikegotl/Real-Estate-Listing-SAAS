namespace ListingStudio.IntegrationTests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Infrastructure.Billing;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class StripeBillingGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatesCustomerCheckoutAndPortalUsingServerControlledStripeRequests()
    {
        var handler = new RecordingHandler();
        var gateway = CreateGateway(handler);
        var organizationId = Guid.NewGuid();

        var customerId = await gateway.CreateCustomerAsync(new BillingProviderCustomerRequest(
            organizationId,
            "Acme Realty",
            "owner@example.com",
            "customer-key"));
        var checkout = await gateway.CreateCheckoutSessionAsync(new BillingProviderCheckoutRequest(
            organizationId,
            customerId,
            SubscriptionPlan.Starter,
            "price_starter",
            "https://listing.test/success",
            "https://listing.test/cancel",
            "checkout-key"));
        var portal = await gateway.CreatePortalSessionAsync(new BillingProviderPortalRequest(
            customerId,
            "https://listing.test/billing",
            "portal-key"));

        Assert.Equal("cus_test", customerId);
        Assert.Equal("https://checkout.stripe.test/session", checkout.Url);
        Assert.Equal("https://billing.stripe.test/session", portal.Url);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.AuthorizationScheme));
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/v1/customers", request.Path);
                Assert.Equal("customer-key", request.IdempotencyKey);
                Assert.Contains("metadata%5Borganization_id%5D=", request.Body, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal("/v1/checkout/sessions", request.Path);
                Assert.Equal("checkout-key", request.IdempotencyKey);
                Assert.Contains("mode=subscription", request.Body, StringComparison.Ordinal);
                Assert.Contains("line_items%5B0%5D%5Bprice%5D=price_starter", request.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("card", request.Body, StringComparison.OrdinalIgnoreCase);
            },
            request =>
            {
                Assert.Equal("/v1/billing_portal/sessions", request.Path);
                Assert.Equal("portal-key", request.IdempotencyKey);
                Assert.Contains("customer=cus_test", request.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void VerifiesSignatureAndParsesSubscriptionWithoutTrustingClientState()
    {
        var gateway = CreateGateway(new RecordingHandler());
        var payload = JsonSerializer.Serialize(new
        {
            id = "evt_verified",
            type = "customer.subscription.updated",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "sub_verified",
                    customer = "cus_verified",
                    status = "active",
                    current_period_start = Now.ToUnixTimeSeconds(),
                    current_period_end = Now.AddMonths(1).ToUnixTimeSeconds(),
                    items = new { data = new[] { new { price = new { id = "price_starter" } } } },
                },
            },
        });

        var parsed = Assert.IsType<BillingProviderSubscriptionEvent>(
            gateway.ParseAndVerifyWebhook(payload, Sign(payload, Now)));
        Assert.Equal("evt_verified", parsed.EventId);
        Assert.Equal("cus_verified", parsed.StripeCustomerId);
        Assert.Equal("sub_verified", parsed.StripeSubscriptionId);
        Assert.Equal("price_starter", parsed.PriceId);
        Assert.Equal("active", parsed.Status);

        Assert.Throws<InvalidOperationException>(() =>
            gateway.ParseAndVerifyWebhook(payload, $"t={Now.ToUnixTimeSeconds()},v1=00"));
        Assert.Throws<InvalidOperationException>(() =>
            gateway.ParseAndVerifyWebhook(payload, Sign(payload, Now.AddMinutes(-10))));
    }

    [Fact]
    public async Task DisabledGatewayRejectsPaidCallsBeforeSendingHttp()
    {
        var handler = new RecordingHandler();
        var gateway = new StripeBillingGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.test") },
            Options.Create(new StripeOptions()),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.CreateCustomerAsync(new BillingProviderCustomerRequest(
                Guid.NewGuid(), "Acme", "owner@example.com", "key")));
        Assert.Empty(handler.Requests);
    }

    private static StripeBillingGateway CreateGateway(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.test") },
        Options.Create(new StripeOptions
        {
            Enabled = true,
            SecretKey = "test-secret-key",
            WebhookSecret = "test-webhook-secret",
            StarterPriceId = "price_starter",
            ProfessionalPriceId = "price_professional",
            WebhookToleranceSeconds = 300,
        }),
        new FixedTimeProvider(Now));

    private static string Sign(string payload, DateTimeOffset timestamp)
    {
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp.ToUnixTimeSeconds()}.{payload}");
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-webhook-secret"),
            signedPayload);
        return $"t={timestamp.ToUnixTimeSeconds()},v1={Convert.ToHexString(signature).ToLowerInvariant()}";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Scheme,
                request.Headers.GetValues("Idempotency-Key").Single(),
                body));
            var responseBody = request.RequestUri?.AbsolutePath switch
            {
                "/v1/customers" => "{\"id\":\"cus_test\"}",
                "/v1/checkout/sessions" => "{\"url\":\"https://checkout.stripe.test/session\"}",
                "/v1/billing_portal/sessions" => "{\"url\":\"https://billing.stripe.test/session\"}",
                _ => throw new InvalidOperationException("Unexpected Stripe test path."),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        string Path,
        string? AuthorizationScheme,
        string IdempotencyKey,
        string Body);
}
