using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignVideoLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecommendedLibraryNeedsItsRuleVersionButALegacyOneDoesNot()
    {
        Assert.Throws<ArgumentException>(() => Library(VerticalVideoSelection.RecommendedThree, null));
        Assert.Null(Library(VerticalVideoSelection.LegacySingle, null).RuleVersion);
        Assert.Throws<ArgumentException>(() => CampaignVideoLibrary.Create(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), VerticalVideoSelection.LegacySingle, null, null, Now));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    public void AcceptsSupportedVerticalLengths(int seconds)
    {
        Assert.Equal(seconds, Library().AddVariant(Plan() with { DurationSeconds = seconds }, Now).DurationSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(90)]
    public void RejectsOtherLengths(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Library().AddVariant(Plan() with { DurationSeconds = seconds }, Now));
    }

    [Fact]
    public void ValidatesHookCallToActionAndRecipe()
    {
        var library = Library();
        Assert.Throws<ArgumentException>(() => library.AddVariant(Plan() with { HookText = new string('a', 101) }, Now));
        Assert.Throws<ArgumentException>(() => library.AddVariant(Plan() with { HookGroundingKey = null }, Now));
        Assert.Throws<ArgumentException>(() => library.AddVariant(Plan() with { CallToAction = new string('a', 81) }, Now));
        Assert.Throws<ArgumentException>(() => library.AddVariant(Plan() with { RecipeVersion = " " }, Now));
        Assert.ThrowsAny<ArgumentException>(() => library.AddVariant(Plan() with { RecipeJson = "" }, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => library.AddVariant(Plan() with { Sequence = 0 }, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => library.AddVariant(Plan() with { Format = (VerticalCreativeFormat)99 }, Now));
        var plain = library.AddVariant(Plan() with { HookText = null, HookGroundingKey = null, CallToAction = " " }, Now);
        Assert.Null(plain.HookText);
        Assert.Null(plain.CallToAction);
    }

    [Fact]
    public void FormatAndPositionAreUniqueInALibrary()
    {
        var library = Library();
        library.AddVariant(Plan(), Now);
        Assert.Throws<InvalidOperationException>(() => library.AddVariant(Plan(), Now));
        library.AddVariant(Plan() with { Sequence = 2 }, Now);
        library.AddVariant(Plan() with { Format = VerticalCreativeFormat.PriceFirst }, Now);
        Assert.Equal(3, library.Variants.Count);
    }

    [Fact]
    public void IneligibleFormatsAreSkippedNotFailed()
    {
        var library = Library();
        var rendered = library.AddVariant(Plan(), Now);
        var skipped = library.AddVariant(Plan() with { Format = VerticalCreativeFormat.StoryReveal, IsEligible = false }, Now);
        Assert.Equal(CampaignVideoVariantStatus.Skipped, skipped.Status);
        Assert.Throws<InvalidOperationException>(() => skipped.MarkGenerating(Now));

        library.MarkGenerating(Now);
        rendered.MarkGenerating(Now);
        rendered.MarkRendered(Now);
        library.Finish(Now);
        Assert.Equal(CampaignVideoLibraryStatus.Completed, library.Status);
        Assert.Throws<InvalidOperationException>(() => library.AddVariant(Plan() with { Sequence = 9 }, Now));
    }

    [Fact]
    public void StatusChangesFollowTheVideoLifecycle()
    {
        var library = Library();
        var video = library.AddVariant(Plan(), Now);
        Assert.Throws<InvalidOperationException>(() => video.MarkRendered(Now));
        Assert.Throws<InvalidOperationException>(() => video.Retry(Now));
        video.MarkGenerating(Now);
        video.MarkGenerating(Now);
        video.MarkFailed(new string('x', 900), Now);
        Assert.Equal(500, video.Error!.Length);
        video.Retry(Now);
        Assert.Equal(CampaignVideoVariantStatus.Planned, video.Status);
        video.MarkGenerating(Now);
        video.MarkRendered(Now);
        Assert.Null(video.Error);
        Assert.Throws<InvalidOperationException>(() => video.MarkFailed("late", Now));
        Assert.Throws<InvalidOperationException>(() => video.MarkGenerating(Now));
        video.Cancel(Now);
        Assert.Equal(CampaignVideoVariantStatus.Rendered, video.Status);
    }

    [Fact]
    public void LibraryOutcomeReflectsItsRequiredVideos()
    {
        var partial = Library();
        var good = partial.AddVariant(Plan(), Now);
        var bad = partial.AddVariant(Plan() with { Format = VerticalCreativeFormat.PriceFirst }, Now);
        Assert.Throws<InvalidOperationException>(() => partial.Finish(Now));
        good.MarkGenerating(Now);
        good.MarkRendered(Now);
        bad.MarkGenerating(Now);
        bad.MarkFailed("Render failed.", Now);
        partial.Finish(Now);
        Assert.Equal(CampaignVideoLibraryStatus.PartiallyCompleted, partial.Status);

        var failed = Library();
        var only = failed.AddVariant(Plan(), Now);
        only.MarkGenerating(Now);
        only.MarkFailed("Render failed.", Now);
        failed.Finish(Now);
        Assert.Equal(CampaignVideoLibraryStatus.Failed, failed.Status);
    }

    [Fact]
    public void CancellingKeepsFinishedVideosAndStopsTheRest()
    {
        var library = Library();
        var done = library.AddVariant(Plan(), Now);
        var waiting = library.AddVariant(Plan() with { Format = VerticalCreativeFormat.PriceFirst }, Now);
        done.MarkGenerating(Now);
        done.MarkRendered(Now);
        library.Cancel(Now);
        Assert.Equal(CampaignVideoLibraryStatus.Cancelled, library.Status);
        Assert.Equal(CampaignVideoVariantStatus.Rendered, done.Status);
        Assert.Equal(CampaignVideoVariantStatus.Cancelled, waiting.Status);
        library.Finish(Now);
        Assert.Equal(CampaignVideoLibraryStatus.Cancelled, library.Status);
    }

    [Fact]
    public void FeatureFlagKillSwitchOverridesThePilotList()
    {
        var pilot = Guid.NewGuid();
        var flag = FeatureFlag.Create(FeatureFlagKeys.VerticalVideoLibraryV1, Now);
        flag.Organizations.Add(FeatureFlagOrganization.Create(flag.Key, pilot, Now));
        Assert.False(flag.IsEnabledFor(pilot));
        flag.Configure(true, false, "admin", Now);
        Assert.True(flag.IsEnabledFor(pilot));
        Assert.False(flag.IsEnabledFor(Guid.NewGuid()));
        flag.Configure(true, true, "admin", Now);
        Assert.True(flag.IsEnabledFor(Guid.NewGuid()));
        flag.Configure(false, true, "admin", Now);
        Assert.False(flag.IsEnabledFor(pilot));
        Assert.Throws<ArgumentException>(() => FeatureFlag.Create("Unknown", Now));
    }

    private static CampaignVideoLibrary Library(
        VerticalVideoSelection selection = VerticalVideoSelection.RecommendedThree,
        string? ruleVersion = "vertical-rules-v1") =>
        CampaignVideoLibrary.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), selection, ruleVersion, null, Now);

    private static CampaignVideoVariantPlan Plan() =>
        new(VerticalCreativeFormat.WowFeature, VerticalCampaignPurpose.Reach, 1, 15, true, "A two-story glass atrium",
            "media.hero", null, CampaignVoiceoverMode.None, "Tour it this weekend", """{"scenes":[]}""", "wow-feature-v1");
}

public sealed class CampaignPresetVerticalSelectionTests
{
    [Fact]
    public void SettingsSavedBeforeTheLibraryAskForOneSharedVerticalVideo()
    {
        var saved = CampaignPresetSettings.Default.ToJson();
        Assert.DoesNotContain("verticalSelection", saved, StringComparison.Ordinal);
        var read = CampaignPresetSettings.FromJson(saved);
        Assert.Equal(VerticalVideoSelection.LegacySingle, read.Settings.VerticalSelection);
        Assert.Empty(read.SkippedValues);
    }

    [Fact]
    public void ChosenLibrarySelectionRoundTripsOnlyWithAVerticalOutput()
    {
        var library = CampaignPresetSettings.Default with { VerticalSelection = VerticalVideoSelection.RecommendedThree };
        Assert.Equal(VerticalVideoSelection.RecommendedThree,
            CampaignPresetSettings.FromJson(library.ToJson()).Settings.VerticalSelection);

        var noVertical = library with { Outputs = CampaignOutputSelection.YouTubeTour };
        Assert.Equal(VerticalVideoSelection.LegacySingle,
            CampaignPresetSettings.FromJson(noVertical.ToJson()).Settings.VerticalSelection);
    }

    [Fact]
    public void UnknownSelectionFallsBackToTheSharedVerticalVideoAndIsReported()
    {
        var json = CampaignPresetSettings.Default.ToJson().TrimEnd('}') + ",\"verticalSelection\":\"FutureMode\"}";
        var read = CampaignPresetSettings.FromJson(json);
        Assert.Equal(VerticalVideoSelection.LegacySingle, read.Settings.VerticalSelection);
        Assert.Contains("FutureMode", read.SkippedValues);
    }
}
