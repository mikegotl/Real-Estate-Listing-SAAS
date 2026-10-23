using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Email;
using ListingStudio.Application.Feedback;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Feedback;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class FeatureSuggestionTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task GeneralAndActiveCampaignSuggestionsAreTenantScoped()
    {
        var owner = await CreateOwnerAndPropertyAsync("suggestion-owner");
        var outsider = await CreateOwnerAndPropertyAsync("suggestion-outsider");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeatureSuggestionService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var general = await service.SubmitAsync(
            owner.UserId,
            owner.PropertyId,
            new FeatureSuggestionInput("Add branded closing animations.", false));
        Assert.NotNull(general);
        Assert.False(general.RequiredForActiveCampaign);
        Assert.Null(general.CampaignGenerationJobId);
        Assert.Null(await service.SubmitAsync(
            outsider.UserId,
            owner.PropertyId,
            new FeatureSuggestionInput("Cross-tenant attempt.", false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            owner.UserId,
            owner.PropertyId,
            new FeatureSuggestionInput("Needed for this campaign.", true)));

        var property = await dbContext.Properties.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == owner.PropertyId);
        var campaign = CampaignGenerationJob.Create(
            property.OrganizationId,
            owner.PropertyId,
            owner.UserId,
            new string('a', 64),
            CampaignOutputSelection.LegacyStandard,
            fixture.Factory.TimeProvider.GetUtcNow());
        dbContext.CampaignGenerationJobs.Add(campaign);
        await dbContext.SaveChangesAsync();

        var required = await service.SubmitAsync(
            owner.UserId,
            owner.PropertyId,
            new FeatureSuggestionInput("Needed for this campaign.", true));
        Assert.NotNull(required);
        Assert.True(required.RequiredForActiveCampaign);
        Assert.Equal(campaign.Id, required.CampaignGenerationJobId);

        var stored = await dbContext.FeatureSuggestions.AsNoTracking()
            .Where(suggestion => suggestion.PropertyId == owner.PropertyId)
            .OrderBy(suggestion => suggestion.SubmittedAtUtc)
            .ToArrayAsync();
        Assert.Equal(2, stored.Length);
        Assert.All(stored, suggestion => Assert.Equal(property.OrganizationId, suggestion.OrganizationId));
        Assert.Equal(campaign.Id, stored[1].CampaignGenerationJobId);
    }

    [Fact]
    public async Task GeneralCommentsAreSavedWithoutAProperty()
    {
        var owner = await CreateOwnerAndPropertyAsync("general-comment");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeatureSuggestionService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var receipt = await service.SubmitAsync(
            owner.UserId,
            null,
            new FeatureSuggestionInput("The billing page needs annual plans.", false, "/billing"));
        Assert.NotNull(receipt);
        Assert.Null(receipt.PropertyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            owner.UserId,
            null,
            new FeatureSuggestionInput("Campaign without a property.", true)));

        var stored = await dbContext.FeatureSuggestions.AsNoTracking().SingleAsync(item => item.Id == receipt.Id);
        Assert.Null(stored.PropertyId);
        Assert.Equal("/billing", stored.PagePath);
        Assert.Null(stored.AdminEmailSentAtUtc);
    }

    [Fact]
    public async Task CommentsAreEmailedOnceToEveryPlatformAdmin()
    {
        var owner = await CreateOwnerAndPropertyAsync("emailed-comment");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var sender = new RecordingEmailSender();
        var processor = new FeatureSuggestionEmailProcessor(
            dbContext,
            sender,
            Options.Create(new FeatureSuggestionEmailOptions { AppBaseUrl = "https://studio.example.com" }),
            fixture.Factory.TimeProvider);

        // Comments from other tests in this class may be waiting too; clear them so this test sees only its own.
        await dbContext.FeatureSuggestions.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.AdminEmailSentAtUtc, fixture.Factory.TimeProvider.GetUtcNow()));
        var receipt = await services.GetRequiredService<IFeatureSuggestionService>().SubmitAsync(
            owner.UserId,
            owner.PropertyId,
            new FeatureSuggestionInput("Please add drone shots.", false, $"/properties/{owner.PropertyId}"));
        Assert.NotNull(receipt);

        // With no platform admin yet, the comment waits without using an attempt.
        var deferred = await processor.ProcessNextAsync();
        Assert.Equal(FeatureSuggestionEmailOutcome.NoRecipients, deferred?.Outcome);
        Assert.Empty(sender.Sent);
        Assert.Null(await processor.ProcessNextAsync());

        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var bootstrapper = services.GetRequiredService<PlatformAdminRoleBootstrapper>();
        var adminEmails = new[] { $"admin-a-{Guid.NewGuid():N}@example.com", $"admin-b-{Guid.NewGuid():N}@example.com" };
        foreach (var email in adminEmails)
        {
            Assert.True((await registration.RegisterAsync(new RegisterAccountCommand(
                email, "Password123", $"Admin {Guid.NewGuid():N}"))).Succeeded);
            Assert.True(await bootstrapper.GrantAsync(email));
        }

        await dbContext.FeatureSuggestions.Where(item => item.Id == receipt.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.AdminEmailNextAttemptAtUtc, (DateTimeOffset?)null));
        sender.FailNext = true;
        var failed = await processor.ProcessNextAsync();
        Assert.Equal(FeatureSuggestionEmailOutcome.Failed, failed?.Outcome);
        Assert.True(failed!.WillRetry);
        Assert.Null(await processor.ProcessNextAsync());

        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var sent = await processor.ProcessNextAsync();
        Assert.Equal(FeatureSuggestionEmailOutcome.Sent, sent?.Outcome);
        Assert.Null(await processor.ProcessNextAsync());

        var message = Assert.Single(sender.Sent);
        Assert.Equal(adminEmails.Order(StringComparer.OrdinalIgnoreCase), message.BlindCopyRecipients);
        Assert.DoesNotContain(owner.Email, message.BlindCopyRecipients);
        Assert.Equal(owner.Email, message.ReplyTo);
        Assert.Contains("Please add drone shots.", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("321 Feedback Lane, Raleigh, NC 27601", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://studio.example.com/properties/{owner.PropertyId}", message.TextBody, StringComparison.Ordinal);
        var stored = await dbContext.FeatureSuggestions.AsNoTracking().SingleAsync(item => item.Id == receipt.Id);
        Assert.NotNull(stored.AdminEmailSentAtUtc);
        Assert.Equal(2, stored.AdminEmailAttemptCount);
    }

    private async Task<OwnerProperty> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var owner = await registration.RegisterAsync(new RegisterAccountCommand(
            email,
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(owner.Succeeded, string.Join(", ", owner.Errors));
        var propertyId = await scope.ServiceProvider.GetRequiredService<IPropertyService>().CreateAsync(
            owner.UserId!,
            new PropertyInput(
                "321 Feedback Lane", null, "Raleigh", "NC", "27601", 525_000m,
                4, 3m, 2_500, 0.3m, 2008, PropertyType.SingleFamily,
                "A test property for product feedback.", ListingStatus.Active));
        return new OwnerProperty(owner.UserId!, propertyId, email);
    }

    private sealed record OwnerProperty(string UserId, Guid PropertyId, string Email);

    private sealed class RecordingEmailSender : IOutboundEmailSender
    {
        public List<OutboundEmail> Sent { get; } = [];

        public bool FailNext { get; set; }

        public bool IsConfigured => true;

        public Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("SMTP server unavailable.");
            }

            Sent.Add(email);
            return Task.CompletedTask;
        }
    }
}
