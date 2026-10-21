using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ShortFormEditorTests
{
    private static readonly Guid[] PhotoIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();

    [Fact]
    public void FastEditCutsOnTheBeatWithAHookAndAnEndCard()
    {
        var options = CampaignCreativeOptions.ForTemplate(CampaignMarketingTemplate.JustListed);
        var edit = Create(options, beatMs: 500);

        Assert.Equal(VideoAspectRatio.Vertical9By16, edit.AspectRatio);
        Assert.Equal(RequestedDuration.Feature30, edit.RequestedDuration);
        Assert.Equal(30_000, edit.Scenes.Sum(scene => scene.DurationMs));
        Assert.Equal(3_000, edit.Scenes[0].DurationMs);
        Assert.All(edit.Scenes.Skip(1).SkipLast(1), scene =>
        {
            Assert.Equal(1_500, scene.DurationMs);
            Assert.Equal(TransitionKind.Cut, scene.TransitionIn.Type);
            Assert.Equal(MotionKind.KenBurns, scene.Motion.Type);
            Assert.NotEqual(scene.Motion.StartViewport, scene.Motion.EndViewport);
        });
        Assert.True(edit.Scenes[^1].DurationMs >= 3_000);
        Assert.Equal(
            ["JUST LISTED", "$725,000", "Raleigh, NC", "4 beds • 3 baths"],
            edit.Scenes[0].TextOverlays.Select(overlay => overlay.Text));
        Assert.Equal(TextOverlayStyle.HookTitle, edit.Scenes[0].TextOverlays[0].StyleToken);
        Assert.Contains(edit.Scenes[^1].TextOverlays, overlay => overlay.StyleToken == TextOverlayStyle.ClosingCta);
        Assert.Contains(edit.Scenes[^1].TextOverlays, overlay => overlay.Text == "Avery Agent • 555-0100");
        Assert.Contains(edit.Scenes[1].TextOverlays, overlay => overlay.Text == "$725,000 • 4 beds • 3 baths");
        Assert.All(edit.Scenes.SelectMany(scene => scene.TextOverlays), overlay =>
            Assert.Single(edit.FactBindings, binding => binding.Key == overlay.GroundingKey && binding.Value == overlay.Text));
    }

    [Fact]
    public void ViewportsKeepTheVerticalShapeOfEveryPhoto()
    {
        var edit = Create(CampaignCreativeOptions.SocialDefault, beatMs: 536);

        foreach (var viewport in edit.Scenes.SelectMany(scene => new[] { scene.Motion.StartViewport, scene.Motion.EndViewport }))
        {
            Assert.InRange(viewport.Width * 1_600 / (viewport.Height * 1_200), 0.5525m, 0.5725m);
            Assert.True(viewport.X >= 0 && viewport.Y >= 0 && viewport.X + viewport.Width <= 1 && viewport.Y + viewport.Height <= 1);
        }
    }

    [Fact]
    public void ShortHookVoiceoverOpensWithTheHookAndEndsWithTheCallToAction()
    {
        var edit = Create(CampaignCreativeOptions.SocialDefault, beatMs: 500);

        var segments = edit.Audio.NarrationSegments;
        Assert.Equal("story.openingHook", segments[0].GroundingKey);
        Assert.Equal("story.closingCta", segments[^1].GroundingKey);
        Assert.Equal(300, segments[0].StartMs);
        Assert.True(segments[^1].StartMs + ShortFormEditor.EstimateMs(segments[^1].Text) <= 30_000);
        Assert.All(segments.Zip(segments.Skip(1)), pair => Assert.True(pair.First.StartMs < pair.Second.StartMs));
        Assert.All(edit.Scenes, scene => Assert.Equal(
            segments.Where(segment => segment.StartMs < scene.StartMs + scene.DurationMs
                    && segment.StartMs + segment.DurationMs > scene.StartMs)
                .Select(segment => segment.Id)
                .Order(StringComparer.Ordinal),
            scene.NarrationSegmentIds));
    }

    [Fact]
    public void NoVoiceoverLeavesOnlyMusicAndText()
    {
        var edit = Create(CampaignCreativeOptions.SocialDefault with { Voiceover = CampaignVoiceoverMode.None }, beatMs: 500);

        Assert.Empty(edit.Audio.NarrationSegments);
        Assert.All(edit.Scenes, scene => Assert.Empty(scene.NarrationSegmentIds));
    }

    [Fact]
    public void FullVoiceoverKeepsTheTourNarrationInASixtySecondEdit()
    {
        var edit = Create(CampaignCreativeOptions.SocialDefault with { Voiceover = CampaignVoiceoverMode.Full }, beatMs: 500);

        Assert.Equal(RequestedDuration.Hero60, edit.RequestedDuration);
        Assert.Equal(60_000, edit.Scenes.Sum(scene => scene.DurationMs));
        Assert.Equal(Master().Audio.NarrationSegments, edit.Audio.NarrationSegments);
    }

    [Fact]
    public void CinematicEditUsesLongerShotsWithCrossfades()
    {
        var edit = Create(CampaignCreativeOptions.SocialDefault with { Pace = CampaignPace.Cinematic }, beatMs: 500);

        Assert.All(edit.Scenes.Skip(1).SkipLast(1), scene =>
        {
            Assert.Equal(4_000, scene.DurationMs);
            Assert.Equal(TransitionKind.Crossfade, scene.TransitionIn.Type);
        });
    }

    [Fact]
    public void PriceDropAndOpenHouseLeadWithTheConfirmedHook()
    {
        var priceDrop = Create(CampaignCreativeOptions.ForTemplate(
            CampaignMarketingTemplate.PriceDrop, priceReductionAmount: 25_000m, priceReductionVerified: true), 500);
        var openHouse = Create(CampaignCreativeOptions.ForTemplate(
            CampaignMarketingTemplate.OpenHouse, openHouseText: "Sat 1-3 PM", openHouseVerified: true), 500);

        Assert.Equal("PRICE REDUCED $25,000", priceDrop.Scenes[0].TextOverlays[0].Text);
        Assert.Equal(["OPEN HOUSE", "Sat 1-3 PM", "$725,000", "Raleigh, NC"],
            openHouse.Scenes[0].TextOverlays.Select(overlay => overlay.Text));
        var applied = CampaignCreativeDirector.Apply(priceDrop, CampaignCreativeOptions.ForTemplate(
            CampaignMarketingTemplate.PriceDrop, priceReductionAmount: 25_000m, priceReductionVerified: true), 2005);
        Assert.Single(applied.Scenes[0].TextOverlays, overlay => overlay.Text.StartsWith("PRICE REDUCED", StringComparison.Ordinal));
    }

    [Fact]
    public void PhotosTheTourSkippedAreUsedInStoryboardOrder()
    {
        // The master tour shows photos 0, 2 and 4; the edit has room for all six.
        var edit = Create(CampaignCreativeOptions.SocialDefault, beatMs: 500, tourPhotos: [0, 2, 4]);

        var shown = edit.Scenes.Skip(1).Select(scene => scene.VisualSource.PropertyMediaId).Distinct().ToArray();
        Assert.Equal([PhotoIds[1], PhotoIds[2], PhotoIds[3], PhotoIds[4], PhotoIds[5], PhotoIds[0]], shown);
        Assert.Equal(PhotoIds[0], edit.Scenes[0].VisualSource.PropertyMediaId);
    }

    [Fact]
    public void AgentContactIsLeftOutWhenTheAgentTurnsItOff()
    {
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.AgentContact,
        };

        var edit = Create(options, 500);

        Assert.DoesNotContain(edit.Scenes.SelectMany(scene => scene.TextOverlays),
            overlay => overlay.StyleToken == TextOverlayStyle.AgentContact);
    }

    private static VideoProductionSpecification Create(
        CampaignCreativeOptions options,
        int beatMs,
        int[]? tourPhotos = null)
    {
        var master = Master(tourPhotos);
        return ShortFormEditor.Create(new ShortFormEditRequest(
            master,
            PhotoIds.Select(id => new ShortFormPhoto(id, 1_600, 1_200)).ToArray(),
            [],
            ShortFormCopyBuilder.DurationFor(options.Voiceover),
            beatMs,
            options.Pace,
            ShortFormCopyBuilder.Build(master, options, "Raleigh", "NC")));
    }

    private static VideoProductionSpecification Master(int[]? tourPhotos = null)
    {
        var tour = tourPhotos ?? [0, 1, 2, 3, 4, 5];
        var full = new NormalizedRect(0, 0.125m, 1, 0.75m);
        var sceneMs = 60_000 / tour.Length;
        var scenes = tour.Select((photo, index) => new VideoScene(
            index + 1,
            index * sceneMs,
            index == tour.Length - 1 ? 60_000 - index * sceneMs : sceneMs,
            new VisualSource(VisualSourceKind.PropertyMedia, PhotoIds[photo], null, null, null),
            new TransitionPlan(TransitionKind.Cut, 0),
            new MotionPlan(MotionKind.KenBurns, full, full, MotionEasing.Linear),
            [],
            [],
            ["narration-1"])).ToArray();
        return new VideoProductionSpecification(
            "1.0", Guid.NewGuid(), Guid.NewGuid(), 1, RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new FactBinding("property.listingPrice", "$725,000", FactSource.VerifiedProperty, "ListingPrice"),
                new FactBinding("property.bedBath", "4 beds • 3 baths", FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
                new FactBinding("story.openingHook", "Light, space and a yard made for summer.", FactSource.PropertyStory, "OpeningHook"),
                new FactBinding("story.highlight.1", "A bright kitchen at the heart of the home.", FactSource.PropertyStory, "Highlights[0]"),
                new FactBinding("story.highlight.2", "A calm primary suite with a spa-like bath.", FactSource.PropertyStory, "Highlights[1]"),
                new FactBinding("story.closingCta", "Book your private showing today.", FactSource.PropertyStory, "ClosingCta"),
                new FactBinding("story.voiceover", "Welcome home.", FactSource.PropertyStory, "VoiceoverScript"),
            ],
            new BrandKit(null, null, "Avery Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Book your private showing today.", "story.closingCta"),
            new AudioPlan(
                [new NarrationSegment("narration-1", 500, 55_000, "Welcome home.", "story.voiceover")],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            scenes);
    }
}
