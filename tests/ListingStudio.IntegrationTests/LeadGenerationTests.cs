using System.Net;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Leads;
using ListingStudio.Application.Notifications;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Leads;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class LeadGenerationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task PublishedListingCapturesAttributedLeadAndNotifiesOrganization()
    {
        var owner = await CreateOwnerAndPropertyAsync("lead-capture");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leads = scope.ServiceProvider.GetRequiredService<ILeadGenerationService>();
        var settings = await leads.SaveCaptureSettingsAsync(
            owner.UserId,
            owner.PropertyId,
            "Request a private tour",
            published: true);

        Assert.NotNull(settings);
        var listing = await leads.GetPublicListingAsync(settings.PublicSlug);
        Assert.NotNull(listing);
        Assert.Equal("Request a private tour", listing.Headline);
        using (var client = fixture.Factory.CreateClient())
        using (var qr = await client.GetAsync($"/public/listings/{settings.PublicSlug}/qr?source=facebook"))
        {
            Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
            Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<svg", await qr.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var submitted = await leads.SubmitAsync(settings.PublicSlug, new PublicLeadInput(
            "Alex Buyer",
            "alex@example.com",
            "919-555-0100",
            LeadPreferredContact.Either,
            "Can I tour on Saturday?",
            EmailMarketingConsent: true,
            SmsMarketingConsent: true,
            "facebook",
            "social",
            "property",
            Website: null));

        Assert.True(submitted.Succeeded, string.Join(' ', submitted.Errors));
        var dashboard = await leads.GetDashboardAsync(owner.UserId);
        var summary = Assert.Single(dashboard.Leads);
        Assert.Equal("facebook", summary.Source);
        Assert.Equal(1, dashboard.New);

        var details = await leads.GetLeadAsync(owner.UserId, submitted.LeadId!.Value);
        Assert.NotNull(details);
        Assert.True(details.EmailMarketingConsent);
        Assert.True(details.SmsMarketingConsent);
        Assert.Single(details.Activities);

        var inbox = await scope.ServiceProvider.GetRequiredService<IUserNotificationService>()
            .GetInboxAsync(owner.UserId);
        var notification = Assert.Single(inbox.Items, item => item.Kind == Domain.Notifications.UserNotificationKind.Lead);
        Assert.Equal($"/leads?propertyId={owner.PropertyId}", notification.Link);

        var duplicate = await leads.SubmitAsync(settings.PublicSlug, new PublicLeadInput(
            "Alex Buyer", "alex@example.com", null, LeadPreferredContact.Email,
            "Sending twice", false, false, null, null, null, null));
        Assert.False(duplicate.Succeeded);
        var bot = await leads.SubmitAsync(settings.PublicSlug, new PublicLeadInput(
            "Bot", "bot@example.com", null, LeadPreferredContact.Email,
            "Spam", false, false, null, null, null, "filled honeypot"));
        Assert.False(bot.Succeeded);
    }

    [Fact]
    public async Task LeadWorkflowAndDataStayInsideOwningOrganization()
    {
        var owner = await CreateOwnerAndPropertyAsync("lead-owner");
        var outsider = await CreateOwnerAndPropertyAsync("lead-outsider");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leads = scope.ServiceProvider.GetRequiredService<ILeadGenerationService>();
        var settings = await leads.SaveCaptureSettingsAsync(owner.UserId, owner.PropertyId, null, true);
        Assert.NotNull(settings);
        var submitted = await leads.SubmitAsync(settings.PublicSlug, new PublicLeadInput(
            "Taylor Prospect", "taylor@example.com", null, LeadPreferredContact.Email,
            "Please send the disclosure package.", false, false, "qr", "offline", "property", null));
        Assert.True(submitted.Succeeded);

        Assert.Null(await leads.GetCaptureSettingsAsync(outsider.UserId, owner.PropertyId));
        Assert.Null(await leads.GetLeadAsync(outsider.UserId, submitted.LeadId!.Value));
        Assert.False(await leads.UpdateWorkflowAsync(
            outsider.UserId, submitted.LeadId.Value, LeadStatus.Qualified, null));
        Assert.False(await leads.AddNoteAsync(outsider.UserId, submitted.LeadId.Value, "Unauthorized note"));
        Assert.False(await leads.DeleteAsync(outsider.UserId, submitted.LeadId.Value));

        var followUp = fixture.Factory.TimeProvider.GetUtcNow().AddDays(1);
        Assert.True(await leads.UpdateWorkflowAsync(
            owner.UserId, submitted.LeadId.Value, LeadStatus.Qualified, followUp));
        Assert.True(await leads.AddNoteAsync(owner.UserId, submitted.LeadId.Value, "Pre-approved buyer."));
        var details = await leads.GetLeadAsync(owner.UserId, submitted.LeadId.Value);
        Assert.NotNull(details);
        Assert.Equal(LeadStatus.Qualified, details.Status);
        Assert.Equal(3, details.Activities.Count);
        Assert.Contains("Taylor Prospect", System.Text.Encoding.UTF8.GetString(await leads.ExportCsvAsync(owner.UserId)));
    }

    [Fact]
    public async Task UnpublishedListingAndLeadCenterHaveCorrectAnonymousBoundaries()
    {
        var owner = await CreateOwnerAndPropertyAsync("lead-boundaries");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var leads = scope.ServiceProvider.GetRequiredService<ILeadGenerationService>();
            var settings = await leads.SaveCaptureSettingsAsync(owner.UserId, owner.PropertyId, null, true);
            Assert.NotNull(settings);

            using var client = fixture.Factory.CreateClient();
            using var publicPage = await client.GetAsync($"/listing/{settings.PublicSlug}");
            Assert.Equal(HttpStatusCode.OK, publicPage.StatusCode);
            Assert.Contains("123 Main Street", await publicPage.Content.ReadAsStringAsync());

            await leads.SaveCaptureSettingsAsync(owner.UserId, owner.PropertyId, null, false);
            Assert.Null(await leads.GetPublicListingAsync(settings.PublicSlug));
            using var unpublishedQr = await client.GetAsync($"/public/listings/{settings.PublicSlug}/qr");
            Assert.Equal(HttpStatusCode.NotFound, unpublishedQr.StatusCode);
        }

        using var anonymous = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var leadCenter = await anonymous.GetAsync("/leads");
        Assert.Equal(HttpStatusCode.Redirect, leadCenter.StatusCode);
        Assert.Equal("/Account/Login", leadCenter.Headers.Location?.AbsolutePath);
        using var export = await anonymous.GetAsync("/leads/export.csv");
        Assert.Equal(HttpStatusCode.Redirect, export.StatusCode);
        Assert.Equal("/Account/Login", export.Headers.Location?.AbsolutePath);
    }

    private async Task<OwnerProperty> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var owner = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(owner.Succeeded, string.Join(", ", owner.Errors));
        var propertyId = await scope.ServiceProvider.GetRequiredService<IPropertyService>().CreateAsync(
            owner.UserId!,
            new PropertyInput(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m,
                3, 2.5m, 2_100, 0.25m, 1998, PropertyType.SingleFamily,
                "A comfortable home.", ListingStatus.Active));
        return new OwnerProperty(owner.UserId!, propertyId);
    }

    private sealed record OwnerProperty(string UserId, Guid PropertyId);
}
