using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class KlingPilotModeTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    [Fact]
    public void PilotMeansOnThroughThePilotListNotForEveryWorkspace()
    {
        var key = FeatureFlagKeysForTest.NativePresenter;
        Assert.True(KlingNativePresenterRelease.IsPilot(new FeatureFlagState(key, true, false, [Workspace], null), Workspace));
        Assert.False(KlingNativePresenterRelease.IsPilot(new FeatureFlagState(key, true, true, [], null), Workspace));
        Assert.False(KlingNativePresenterRelease.IsPilot(new FeatureFlagState(key, true, true, [Workspace], null), Workspace));
        Assert.False(KlingNativePresenterRelease.IsPilot(new FeatureFlagState(key, false, false, [Workspace], null), Workspace));
        Assert.False(KlingNativePresenterRelease.IsPilot(new FeatureFlagState(key, true, false, [], null), Workspace));
    }

    [Fact]
    public void OnlyThePilotWowFeatureIsTenSecondsAt720p()
    {
        Assert.Equal(10, NativePresenterVideoPolicy.DurationFor(VerticalCreativeFormat.WowFeature, 15, pilot: true));
        Assert.Equal(15, NativePresenterVideoPolicy.DurationFor(VerticalCreativeFormat.WowFeature, 15, pilot: false));
        Assert.Equal(30, NativePresenterVideoPolicy.DurationFor(VerticalCreativeFormat.PriceFirst, 30, pilot: true));
        Assert.Equal("720p", NativePresenterVideoPolicy.ResolutionFor(VerticalCreativeFormat.WowFeature, 10));
        Assert.Null(NativePresenterVideoPolicy.ResolutionFor(VerticalCreativeFormat.WowFeature, 15));
        Assert.Null(NativePresenterVideoPolicy.ResolutionFor(VerticalCreativeFormat.PriceFirst, 30));
    }

    [Fact]
    public void RetimingKeepsEveryBeatInOrderAndEndsExactlyOnTheNewLength()
    {
        var wow = VerticalCreativePlan.Create(RecipeFixture.Rich()).Recipes
            .Single(recipe => recipe.Format == VerticalCreativeFormat.WowFeature);
        Assert.Equal(15, wow.DurationSeconds);

        var shorter = wow.RetimedTo(NativePresenterVideoPolicy.PilotWowFeatureSeconds);

        Assert.Equal(10, shorter.DurationSeconds);
        Assert.Empty(shorter.Validate());
        Assert.Equal(wow.Beats.Count, shorter.Beats.Count);
        Assert.Equal(10_000, shorter.Beats.Sum(beat => beat.DurationMs));
        Assert.Equal(
            wow.Beats.OrderBy(beat => beat.Order).Select(beat => (beat.Kind, beat.PropertyMediaId, beat.Overlay)),
            shorter.Beats.OrderBy(beat => beat.Order).Select(beat => (beat.Kind, beat.PropertyMediaId, beat.Overlay)));
        // The 3-second hook keeps its share: two seconds of ten.
        Assert.Equal(2_000, shorter.Beats.OrderBy(beat => beat.Order).First().DurationMs);
        Assert.Same(wow, wow.RetimedTo(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => wow.RetimedTo(12));
    }

    [Fact]
    public void PreviewImageSlotsLinkThisSitesCopyOfEachPhoto()
    {
        var site = new Uri("https://creativelistingstudio.com/");
        var media = Guid.NewGuid();
        Assert.Equal(
            $"https://creativelistingstudio.com/property-media/{media}",
            ListingStudio.Infrastructure.Campaigns.CampaignGenerationService.PreviewImageUrl(site, $"property-media/{media}", "photo 1, Kitchen"));
        Assert.Equal(
            ListingStudio.Infrastructure.Campaigns.CampaignGenerationService.PreviewImageUrlPlaceholder("photo 1, Kitchen"),
            ListingStudio.Infrastructure.Campaigns.CampaignGenerationService.PreviewImageUrl(null, $"property-media/{media}", "photo 1, Kitchen"));
        Assert.Equal(
            ListingStudio.Infrastructure.Campaigns.CampaignGenerationService.PreviewImageUrlPlaceholder("your profile headshot"),
            ListingStudio.Infrastructure.Campaigns.CampaignGenerationService.PreviewImageUrl(site, null, "your profile headshot"));
    }

    private static class FeatureFlagKeysForTest
    {
        public const string NativePresenter = ListingStudio.Domain.Administration.FeatureFlagKeys.Kling4NativePresenterV1;
    }
}
