namespace ListingStudio.IntegrationTests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class BillingTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task BillingPageRedirectsAnonymousUserToLogin()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/billing");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task TestCustomerCanCheckoutOpenPortalAndUpdateBillingFromVerifiedIdempotentWebhooks()
    {
        var registration = await RegisterAsync("billing-owner");
        var customerCallsBefore = fixture.Factory.BillingProvider.CustomerCallCount;
        var checkoutCallsBefore = fixture.Factory.BillingProvider.CheckoutCallCount;
        var portalCallsBefore = fixture.Factory.BillingProvider.PortalCallCount;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var initial = await billing.GetOverviewAsync(registration.UserId!);
            Assert.Equal(SubscriptionPlan.Free, initial.Plan);
            Assert.Equal(BillingSubscriptionStatus.None, initial.SubscriptionStatus);
            Assert.Null(initial.StripeCustomerId);

            var checkout = await billing.CreateCheckoutSessionAsync(
                registration.UserId!,
                SubscriptionPlan.Starter,
                "https://listing.test/billing?checkout=success",
                "https://listing.test/billing?checkout=cancelled");
            Assert.Equal("https://checkout.stripe.test/session", checkout.Url);
            Assert.Equal(customerCallsBefore + 1, fixture.Factory.BillingProvider.CustomerCallCount);
            Assert.Equal(checkoutCallsBefore + 1, fixture.Factory.BillingProvider.CheckoutCallCount);
            Assert.Equal(FakeBillingProviderGateway.StarterPriceId,
                fixture.Factory.BillingProvider.LastCheckoutRequest?.PriceId);

            var portal = await billing.CreatePortalSessionAsync(
                registration.UserId!,
                "https://listing.test/billing");
            Assert.Equal("https://billing.stripe.test/session", portal.Url);
            Assert.Equal(portalCallsBefore + 1, fixture.Factory.BillingProvider.PortalCallCount);
        }

        var now = DateTimeOffset.UtcNow;
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var eventId = $"evt_{Guid.NewGuid():N}";
        var payload = SubscriptionPayload(
            eventId,
            now,
            registration.OrganizationId!.Value,
            "active",
            periodStart,
            periodStart.AddMonths(1));
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/billing/stripe-webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", Signature(payload));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var overview = await billing.GetOverviewAsync(registration.UserId!);
            Assert.Equal(SubscriptionPlan.Starter, overview.Plan);
            Assert.Equal(BillingSubscriptionStatus.Active, overview.SubscriptionStatus);
            Assert.Equal(2, overview.MonthlyCampaignAllowance);
            Assert.Equal($"cus_test_{registration.OrganizationId:N}", overview.StripeCustomerId);
            Assert.StartsWith("sub_test_", overview.StripeSubscriptionId, StringComparison.Ordinal);

            Assert.Equal(
                BillingWebhookOutcome.AlreadyProcessed,
                await billing.ProcessWebhookAsync(payload, Signature(payload)));

            var olderPayload = SubscriptionPayload(
                $"evt_{Guid.NewGuid():N}",
                now.AddMinutes(-1),
                registration.OrganizationId.Value,
                "canceled",
                periodStart,
                periodStart.AddMonths(1));
            Assert.Equal(
                BillingWebhookOutcome.Processed,
                await billing.ProcessWebhookAsync(olderPayload, Signature(olderPayload)));
            Assert.Equal(
                BillingSubscriptionStatus.Active,
                (await billing.GetOverviewAsync(registration.UserId!)).SubscriptionStatus);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                billing.ProcessWebhookAsync(
                    SubscriptionPayload(
                        $"evt_{Guid.NewGuid():N}",
                        now,
                        registration.OrganizationId.Value,
                        "past_due",
                        periodStart,
                        periodStart.AddMonths(1)),
                    $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},v1=invalid"));

            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(2, await dbContext.BillingWebhookReceipts.CountAsync(receipt =>
                receipt.EventId == eventId || receipt.EventType == "customer.subscription.updated"));
        }
    }

    [Fact]
    public async Task BillingStateIsRestrictedToTheUsersOrganization()
    {
        var owner = await RegisterAsync("billing-tenant-a");
        var outsider = await RegisterAsync("billing-tenant-b");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
        await billing.CreateCheckoutSessionAsync(
            owner.UserId!,
            SubscriptionPlan.Professional,
            "https://listing.test/success",
            "https://listing.test/cancel");

        var ownerOverview = await billing.GetOverviewAsync(owner.UserId!);
        var outsiderOverview = await billing.GetOverviewAsync(outsider.UserId!);
        Assert.NotNull(ownerOverview.StripeCustomerId);
        Assert.Null(outsiderOverview.StripeCustomerId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            billing.CreatePortalSessionAsync(outsider.UserId!, "https://listing.test/billing"));
    }

    private async Task<AccountRegistrationResult> RegisterAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return result;
    }

    private static string SubscriptionPayload(
        string eventId,
        DateTimeOffset eventCreated,
        Guid organizationId,
        string status,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd) => JsonSerializer.Serialize(new
        {
            id = eventId,
            type = "customer.subscription.updated",
            created = eventCreated.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = $"sub_test_{organizationId:N}",
                    customer = $"cus_test_{organizationId:N}",
                    status,
                    current_period_start = periodStart.ToUnixTimeSeconds(),
                    current_period_end = periodEnd.ToUnixTimeSeconds(),
                    items = new
                    {
                        data = new[]
                        {
                            new { price = new { id = FakeBillingProviderGateway.StarterPriceId } },
                        },
                    },
                },
            },
        });

    private static string Signature(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(FakeBillingProviderGateway.WebhookSecret),
            signedPayload);
        return $"t={timestamp},v1={Convert.ToHexString(signature).ToLowerInvariant()}";
    }
}
