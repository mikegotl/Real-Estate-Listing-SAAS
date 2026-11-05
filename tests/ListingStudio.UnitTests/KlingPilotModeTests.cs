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
    public void EveryPilotVideoIsFifteenSecondsAt720pAndLiveKeepsItsLengthAt1080p()
    {
        foreach (var recipeSeconds in new[] { 15, 30, 45, 60 })
        {
            Assert.Equal(15, NativePresenterVideoPolicy.DurationFor(recipeSeconds, pilot: true));
            Assert.Equal(recipeSeconds, NativePresenterVideoPolicy.DurationFor(recipeSeconds, pilot: false));
        }

        Assert.Equal("720p", NativePresenterVideoPolicy.ResolutionFor(pilot: true));
        Assert.Null(NativePresenterVideoPolicy.ResolutionFor(pilot: false));
    }

    [Theory]
    [InlineData(VerticalCreativeFormat.PriceFirst)]
    [InlineData(VerticalCreativeFormat.HostedPov)]
    [InlineData(VerticalCreativeFormat.StoryReveal)]
    public void RetimingKeepsEveryBeatInOrderAndEndsExactlyOnTheNewLength(VerticalCreativeFormat format)
    {
        var recipe = VerticalCreativePlan.Create(RecipeFixture.Rich()).Recipes.Single(candidate => candidate.Format == format);
        Assert.True(recipe.DurationSeconds > 15);

        var shorter = recipe.RetimedTo(NativePresenterVideoPolicy.PilotDurationSeconds);

        Assert.Equal(15, shorter.DurationSeconds);
        Assert.Empty(shorter.Validate());
        Assert.Equal(recipe.Beats.Count, shorter.Beats.Count);
        Assert.Equal(15_000, shorter.Beats.Sum(beat => beat.DurationMs));
        Assert.All(shorter.Beats, beat => Assert.True(beat.DurationMs > 0));
        Assert.Equal(
            recipe.Beats.OrderBy(beat => beat.Order).Select(beat => (beat.Kind, beat.PropertyMediaId, beat.Overlay)),
            shorter.Beats.OrderBy(beat => beat.Order).Select(beat => (beat.Kind, beat.PropertyMediaId, beat.Overlay)));
        Assert.Same(recipe, recipe.RetimedTo(recipe.DurationSeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.RetimedTo(12));
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
