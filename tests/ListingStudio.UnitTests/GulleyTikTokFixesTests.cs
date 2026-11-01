using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>
/// The problems found in the Gulley TikTok review: a "hosted" video made from an unconfirmed phone walk-through with no
/// voice, a price video that went quiet after eight seconds and spelled out "FL" and "sq ft", and a Wow hook that cut
/// to the outside of the house.
/// </summary>
public sealed class GulleyTikTokFixesTests
{
    private const string Story = """
        Step inside this bright, welcoming home.
        To the left, a large, bright kitchen with a big island for prep.
        Next, the living room is spacious and filled with natural light.
        The dining room opens to a sunny back patio.
        Out back, the patio is ready for evenings outside.
        """;

    [Theory]
    [InlineData("$400,000 in Wildwood, FL", "$400,000 in Wildwood, Florida")]
    [InlineData("3 beds • 2 baths • 2,000 sq ft", "3 beds, 2 baths, 2,000 square feet")]
    [InlineData("3 beds • 2.5 baths • 2,100 sq. ft.", "3 beds, 2 and a half baths, 2,100 square feet")]
    [InlineData("Raleigh, NC", "Raleigh, North Carolina")]
    [InlineData("FLOORS and a GA tour", "FLOORS and a GA tour")]
    [InlineData("Ask for the full listing details", "Ask for the full listing details")]
    public void ListingShorthandIsSpokenInWords(string text, string spoken) =>
        Assert.Equal(spoken, SpeechText.ForSpeech(text));

    [Fact]
    public void TheStoryVoiceoverBecomesOneSpokenLinePerSentence()
    {
        var lines = VerticalStoryLines.From("A 2.5 bath home. Bright kitchen!\n\nThe patio is next?");

        Assert.Equal(["A 2.5 bath home.", "Bright kitchen!", "The patio is next?"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.False(line.FromAgent));
        Assert.Equal(lines.Count, lines.Select(line => line.GroundingKey).Distinct().Count());
        Assert.Empty(VerticalStoryLines.From("  "));
    }

    [Fact]
    public void AnUntaggedPhoneWalkthroughIsNotPlannedAsTheAgentHosting()
    {
        // The fixture's walk-through has sound and runs 40 s, so it is only *suggested* as host footage.
        var input = RecipeFixture.Input() with { StoryNarration = VerticalStoryLines.From(Story) };
        Assert.Contains(input.Footage, video => video.IsHostFootage);
        Assert.DoesNotContain(input.Footage, video => video.IsConfirmedHostFootage);

        // Without a confirmed host, narration or presenter there is no hosted video at all.
        Assert.Null(new HostedPovPlanner().Plan(input));
    }

    [Fact]
    public void HostedPovWithAPresenterIsVoicedFromTheHookToTheCallToAction()
    {
        var input = RecipeFixture.Input() with
        {
            StoryNarration = VerticalStoryLines.From(Story),
            Presenter = new VerticalPresenterInput(true, true),
        };

        var recipe = new HostedPovPlanner().Plan(input)!;

        Assert.True(recipe.UsesPresenter);
        Assert.Equal(VerticalVisualTreatment.Presenter, recipe.Beats[0].Treatment);
        Assert.NotNull(recipe.Beats[0].Narration);
        Assert.Equal(recipe.CallToAction.GroundingKey, recipe.Beats[^1].Narration!.GroundingKey);
        Assert.DoesNotContain(recipe.Beats, beat => beat.PropertyVideoId is not null);
        Assert.NotNull(recipe.PrimaryPropertyMediaId);
        var walk = recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.HostedWalk).ToArray();
        Assert.True(walk.Count(beat => beat.Narration is not null) >= 3);
        Assert.All(walk, beat => Assert.InRange(beat.DurationMs, 1, 4_000));
        Assert.Contains(recipe.Facts, fact => fact.Source == VerticalCopySource.PropertyStory);
    }

    [Fact]
    public void PriceFirstKeepsTalkingThroughTheRoomsAndEndsInside()
    {
        var input = RecipeFixture.Input() with { StoryNarration = VerticalStoryLines.From(Story) };

        var recipe = new PriceFirstPlanner().Plan(input)!;

        var tour = recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.RoomTour).ToArray();
        Assert.True(tour.Count(beat => beat.Narration is not null) >= 2);
        Assert.All(tour, beat => Assert.InRange(beat.DurationMs, 1, 4_000));
        Assert.Equal(RecipeFixture.Front, recipe.Beats[0].PropertyMediaId);
        Assert.NotEqual(RecipeFixture.Front, recipe.Beats[^1].PropertyMediaId);
        Assert.Equal(RecipeFixture.Kitchen, recipe.Beats[^1].PropertyMediaId);

        // Every voice line ends before the closing card's call to action starts.
        var recapStart = recipe.Beats[^1].StartMs;
        Assert.All(tour.Where(beat => beat.Narration is not null), beat =>
            Assert.True(beat.StartMs + RecipeBuilderSpeakingMs(beat.Narration!.Text) <= recapStart));
    }

    [Fact]
    public void WowFeatureStaysIndoorsAfterAnIndoorHook()
    {
        var input = RecipeFixture.Input();
        var recipe = new WowFeaturePlanner().Plan(input)!;
        var photos = input.Photos.ToDictionary(photo => photo.MediaId);

        Assert.Equal(RecipeFixture.Kitchen, recipe.Beats[0].PropertyMediaId);
        Assert.All(recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.FeatureDetail), beat =>
            Assert.NotEqual(true, photos[beat.PropertyMediaId!.Value].IsExterior));
    }

    [Fact]
    public void AFormatWithoutThePresenterStillGetsValidOptionsWhenOnlyTheAiPresenterWasChosen()
    {
        var campaign = ListingStudio.Domain.Campaigns.CampaignCreativeOptions.SocialDefault with
        {
            Elements = ListingStudio.Domain.Campaigns.CampaignCreativeOptions.SocialDefault.Elements
                | ListingStudio.Domain.Campaigns.CampaignVideoElement.AgentAvatar,
            AgentPortraitId = Guid.NewGuid(),
            AgentAvatarConsentConfirmed = true,
        };
        campaign.Validate();
        var wow = new WowFeaturePlanner().Plan(RecipeFixture.Input())!;

        var options = VerticalVariantSpecificationBuilder.OptionsFor(campaign, wow);

        options.Validate();
        Assert.False(options.Elements.HasFlag(ListingStudio.Domain.Campaigns.CampaignVideoElement.AgentAvatar));
        Assert.Null(options.AgentPortraitId);
    }

    [Theory]
    [InlineData("""{"width":1920,"height":1080,"side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]}""", true)]
    [InlineData("""{"width":1920,"height":1080,"tags":{"rotate":"270"}}""", true)]
    [InlineData("""{"width":1920,"height":1080,"side_data_list":[{"rotation":180}]}""", false)]
    [InlineData("""{"width":1920,"height":1080}""", false)]
    public void AnUprightPhoneClipStoredSidewaysIsReportedUpright(string stream, bool quarterTurn)
    {
        using var json = System.Text.Json.JsonDocument.Parse(stream);

        Assert.Equal(quarterTurn, ListingStudio.Video.Processing.FfmpegPropertyVideoTranscoder.IsQuarterTurn(json.RootElement));
    }

    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(1080, 1920, false)]
    [InlineData(2160, 3840, false)]
    public void ASidewaysClipGetsTheVerticalShootingTip(int width, int height, bool tip)
    {
        var video = new ListingStudio.Application.Properties.PropertyVideoItem(
            Guid.NewGuid(), "walk.mov", "video/quicktime", 1, width, height, 30_000, 30, true, 0, null,
            DateTimeOffset.UnixEpoch, ListingStudio.Domain.Properties.PropertyVideoProcessingStatus.Completed, 1, null,
            null, null, null, null, null, null);

        Assert.Equal(tip, ListingStudio.Web.Components.Properties.PropertyVideoManager.ShootingTip(video) is not null);
    }

    private static int RecipeBuilderSpeakingMs(string text) =>
        (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 400) + 250;
}
