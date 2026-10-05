namespace ListingStudio.Infrastructure.Billing;

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Billing;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

public sealed class StripeBillingGateway(
    HttpClient httpClient,
    IOptions<StripeOptions> options,
    TimeProvider timeProvider) : IBillingProviderGateway
{
    public async Task<string> CreateCustomerAsync(
        BillingProviderCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>
        {
            ["email"] = request.OwnerEmail,
            ["name"] = request.OrganizationName,
            ["metadata[organization_id]"] = request.OrganizationId.ToString("D", CultureInfo.InvariantCulture),
        };
        using var document = await SendAsync("/v1/customers", values, request.IdempotencyKey, cancellationToken);
        return RequiredString(document.RootElement, "id", "Stripe did not return a customer identifier.");
    }

    public async Task<BillingRedirect> CreateCheckoutSessionAsync(
        BillingProviderCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        var organizationId = request.OrganizationId.ToString("D", CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["customer"] = request.StripeCustomerId,
            ["client_reference_id"] = organizationId,
            ["line_items[0][price]"] = request.PriceId,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = request.SuccessUrl,
            ["cancel_url"] = request.CancelUrl,
            ["allow_promotion_codes"] = "true",
            ["metadata[organization_id]"] = organizationId,
            ["subscription_data[metadata][organization_id]"] = organizationId,
        };
        using var document = await SendAsync("/v1/checkout/sessions", values, request.IdempotencyKey, cancellationToken);
        return new BillingRedirect(RequiredString(document.RootElement, "url", "Stripe did not return a checkout URL."));
    }

    public async Task<BillingRedirect> CreatePortalSessionAsync(
        BillingProviderPortalRequest request,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>
        {
            ["customer"] = request.StripeCustomerId,
            ["return_url"] = request.ReturnUrl,
        };
        using var document = await SendAsync(
            "/v1/billing_portal/sessions",
            values,
            request.IdempotencyKey,
            cancellationToken);
        return new BillingRedirect(RequiredString(document.RootElement, "url", "Stripe did not return a portal URL."));
    }

    public object ParseAndVerifyWebhook(string payload, string signatureHeader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.WebhookSecret))
        {
            throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");
        }

        var signatures = ParseSignatureHeader(signatureHeader, out var timestamp);
        var created = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (Math.Abs((timeProvider.GetUtcNow() - created).TotalSeconds) > settings.WebhookToleranceSeconds)
        {
            throw new InvalidOperationException("The Stripe webhook signature timestamp is outside the allowed tolerance.");
        }

        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{payload}");
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret), signedPayload);
        var valid = signatures.Any(signature =>
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), expected);
            }
            catch (FormatException)
            {
                return false;
            }
        });
        if (!valid)
        {
            throw new InvalidOperationException("The Stripe webhook signature is invalid.");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventId = RequiredString(root, "id", "Stripe event id is missing.");
        var eventType = RequiredString(root, "type", "Stripe event type is missing.");
        var eventCreated = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("created").GetInt64());
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var dataObject = root.GetProperty("data").GetProperty("object");

        if (eventType is "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted")
        {
            var item = dataObject.GetProperty("items").GetProperty("data")[0];
            var periodStart = OptionalInt64(dataObject, "current_period_start")
                ?? OptionalInt64(item, "current_period_start")
                ?? throw new InvalidOperationException("Stripe subscription period start is missing.");
            var periodEnd = OptionalInt64(dataObject, "current_period_end")
                ?? OptionalInt64(item, "current_period_end")
                ?? throw new InvalidOperationException("Stripe subscription period end is missing.");
            return new BillingProviderSubscriptionEvent(
                eventId,
                eventType,
                eventCreated,
                payloadSha256,
                RequiredString(dataObject, "customer", "Stripe customer id is missing."),
                RequiredString(dataObject, "id", "Stripe subscription id is missing."),
                RequiredString(item.GetProperty("price"), "id", "Stripe price id is missing."),
                RequiredString(dataObject, "status", "Stripe subscription status is missing."),
                DateTimeOffset.FromUnixTimeSeconds(periodStart),
                DateTimeOffset.FromUnixTimeSeconds(periodEnd));
        }

        if (eventType == "checkout.session.completed")
        {
            var organizationValue = RequiredString(
                dataObject,
                "client_reference_id",
                "Stripe checkout client reference is missing.");
            if (!Guid.TryParse(organizationValue, out var organizationId))
            {
                throw new InvalidOperationException("Stripe checkout client reference is invalid.");
            }

            return new BillingProviderCheckoutCompletedEvent(
                eventId,
                eventType,
                eventCreated,
                payloadSha256,
                organizationId,
                RequiredString(dataObject, "customer", "Stripe customer id is missing."),
                RequiredString(dataObject, "subscription", "Stripe subscription id is missing."));
        }

        return new BillingProviderIgnoredEvent(eventId, eventType, eventCreated, payloadSha256);
    }

    private async Task<JsonDocument> SendAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            throw new InvalidOperationException("Stripe billing is disabled or Stripe:SecretKey is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(values),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Stripe request failed with HTTP {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(body);
    }

    private static List<string> ParseSignatureHeader(string header, out long timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        timestamp = 0;
        var signatures = new List<string>();
        foreach (var component in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = component.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0] == "t")
            {
                _ = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
            }
            else if (parts[0] == "v1")
            {
                signatures.Add(parts[1]);
            }
        }

        if (timestamp <= 0 || signatures.Count == 0)
        {
            throw new InvalidOperationException("The Stripe-Signature header is malformed.");
        }

        return signatures;
    }

    private static string RequiredString(JsonElement element, string propertyName, string error)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(error);
        }

        return property.GetString()!;
    }

    private static long? OptionalInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
}
