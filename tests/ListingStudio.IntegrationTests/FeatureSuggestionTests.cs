using ListingStudio.Application.Authentication;
using ListingStudio.Application.Feedback;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
                "321 Feedback Lane", null, "Raleigh", "NC", "27601", 525_000m,
                4, 3m, 2_500, 0.3m, 2008, PropertyType.SingleFamily,
                "A test property for product feedback.", ListingStatus.Active));
        return new OwnerProperty(owner.UserId!, propertyId);
    }

    private sealed record OwnerProperty(string UserId, Guid PropertyId);
}
