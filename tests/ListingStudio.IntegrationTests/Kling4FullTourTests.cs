using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task KlingFullTourRequestIsStoredOnlyWhileItsWorkspaceReleaseSwitchIsOn()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-strategy");
        await GrantReviewAdminAsync(owner.UserId);
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload(
                    "exterior.png",
                    "image/png",
                    exterior.Length,
                    exterior,
                    WalkthroughStoryboard.ExteriorFront));
            await using var entry = new MemoryStream(OnePixelPng);
            await media.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload("entry.png", "image/png", entry.Length, entry, "Entry foyer"));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);

        Assert.True(await campaigns.IsKlingFullTourEnabledAsync(owner.UserId));
        var pilot = (await campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.TikTok,
            CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Kling4FullTour,
            new VerticalLibraryRequest(
                VerticalVideoSelection.RecommendedThree,
                VerticalCampaignGoal.MaximizeReach)))!;
        Assert.Equal(CampaignVideoStrategy.Kling4FullTour, pilot.VideoStrategy);
        Assert.Equal(VerticalCampaignGoal.MaximizeReach, pilot.VerticalRequest?.Goal);
        Assert.Equal(
            CampaignVideoStrategy.Kling4FullTour,
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignGenerationJobs
                .Where(candidate => candidate.Id == pilot.Id)
                .Select(candidate => candidate.VideoStrategy)
                .SingleAsync());
        await campaigns.CancelAsync(owner.UserId, pilot.Id);

        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
        Assert.False(await campaigns.IsKlingFullTourEnabledAsync(owner.UserId));
        var control = (await campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.FacebookFeed,
            CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Kling4FullTour))!;
        Assert.Equal(CampaignVideoStrategy.Established, control.VideoStrategy);
        await campaigns.CancelAsync(owner.UserId, control.Id);
    }

    [Fact]
    public async Task KlingFullTourIsGeneratedOnceAndSuppliedOnlyToTheThirtySecondRender()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-render");
        await GrantReviewAdminAsync(owner.UserId);
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
            await using var entry = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "entry.png", "image/png", entry.Length, entry, "Entry foyer"));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
        {
            Category = PropertyMediaCategory.Entry,
            RoomType = "Entry foyer",
        });
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);

        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4FullTourV1,
                globallyEnabled: true,
                enabledForAllOrganizations: false,
                [owner.OrganizationId]);
            queued = (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                CampaignCreativeOptions.SocialDefault,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4FullTour))!;
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(CampaignVideoStrategy.Kling4FullTour, completed.VideoStrategy);
        Assert.Equal(1, fixture.Factory.VideoRenderer.FullTourInputCount);
        Assert.NotNull(fixture.Factory.VideoRenderer.LastRequest?.FullTour);
        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CampaignGenerationJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == queued.Id);
        Assert.NotNull(stored.FullTourAssetPath);
        Assert.Equal(30_000, stored.FullTourDurationMs);

        await verifyScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
    }
}
