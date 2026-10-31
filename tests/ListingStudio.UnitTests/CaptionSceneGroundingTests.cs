using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CaptionSceneGroundingTests
{
    private static readonly NormalizedRect Full = new(0, 0, 1, 1);

    [Theory]
    [InlineData("The kitchen has a large island.", "kitchen")]
    [InlineData("Upstairs the primary bedroom feels calm.", "bedroom")]
    [InlineData("Out back the patio is ready for summer.", "outdoor")]
    [InlineData("Step through the front door into a bright foyer.", "entry")]
    [InlineData("The garden is mature and shaded.", "outdoor")]
    public void NamesTheRoomALineDescribes(string line, string room)
    {
        Assert.Equal([room], CaptionSceneGrounding.RoomsNamedIn(line));
    }

    [Theory]
    [InlineData("Welcome to this home on a quiet street.")]
    [InlineData("It offers 3 bedrooms and 2 bathrooms.")]
    [InlineData("Three bedrooms, two bathrooms, one great price.")]
    [InlineData("   ")]
    public void TreatsGeneralLinesAndRoomCountsAsGeneral(string line)
    {
        Assert.Empty(CaptionSceneGrounding.RoomsNamedIn(line));
    }

    [Theory]
    [InlineData("Exterior front", "outdoor")]
    [InlineData("Entry / foyer", "entry")]
    [InlineData("living_room", "living")]
    [InlineData("Primary bedroom", "bedroom")]
    [InlineData("Kitchen", "kitchen")]
    [InlineData("Bonus space", null)]
    [InlineData(null, null)]
    public void MapsStoryboardStopsAndRoomTypesToRooms(string? label, string? room)
    {
        Assert.Equal(room, CaptionSceneGrounding.CanonicalRoom(label));
    }

    [Theory]
    [InlineData("caption-3-narration-01-0", "narration-01")]
    [InlineData("caption-12-short-story.openingHook-2", "short-story.openingHook")]
    [InlineData("closing-cta", null)]
    public void ReadsTheNarrationLineOfACaption(string overlayId, string? segmentId)
    {
        Assert.Equal(segmentId, CaptionSceneGrounding.SegmentIdOf(overlayId));
    }

    [Theory]
    [InlineData("  Line one\nline two\tthree  ", "Line one line two three")]
    [InlineData("​​", "")]
    [InlineData("   \r\n ", "")]
    [InlineData("La cocina tiene una isla.", "La cocina tiene una isla.")]
    [InlineData("Café nearby", "Café nearby")]
    public void CleansCaptionTextForASingleLineOverlay(string text, string expected)
    {
        Assert.Equal(expected, CaptionSceneGrounding.CleanCaptionText(text));
    }

    [Fact]
    public void RoomCaptionIsNotBurnedInOverAnotherRoom()
    {
        var (kitchen, dining) = (Guid.NewGuid(), Guid.NewGuid());
        // The cut shows the dining room while the voice describes the kitchen.
        var edit = Vertical(
            [Scene(1, 0, 10_000, dining)],
            [new NarrationSegment("narration-01", 0, 9_000, "The kitchen has a large island.", "story.voiceover.1")]);
        var grounding = CaptionSceneGrounding.FromRooms(new Dictionary<Guid, string?>
        {
            [kitchen] = "Kitchen",
            [dining] = "Dining room",
        });

        var ungrounded = CampaignCreativeDirector.Apply(edit, Options(), null, narrationTiming: Timing(edit));
        var grounded = CampaignCreativeDirector.Apply(
            edit, Options(), null, narrationTiming: Timing(edit), captionGrounding: grounding);

        Assert.Contains(ungrounded.Scenes[0].TextOverlays, CampaignVideoEditor.IsCaption);
        Assert.NotEmpty(CaptionSceneGrounding.FindMismatches(ungrounded, grounding));
        Assert.DoesNotContain(grounded.Scenes[0].TextOverlays, CampaignVideoEditor.IsCaption);
        Assert.Empty(CaptionSceneGrounding.FindMismatches(grounded, grounding));
    }

    [Fact]
    public void RoomCaptionStaysOverItsRoomAndGeneralCaptionsStayEverywhere()
    {
        var (kitchen, dining) = (Guid.NewGuid(), Guid.NewGuid());
        var edit = Vertical(
            [Scene(1, 0, 5_000, dining), Scene(2, 5_000, 5_000, kitchen)],
            [
                new NarrationSegment("narration-01", 0, 4_500, "Welcome to this lovely home.", "story.voiceover.1"),
                new NarrationSegment("narration-02", 5_000, 4_500, "The kitchen has a big island.", "story.voiceover.2"),
            ]);
        var grounding = CaptionSceneGrounding.FromRooms(new Dictionary<Guid, string?>
        {
            [kitchen] = "Kitchen",
            [dining] = "Dining room",
        });

        var result = CampaignCreativeDirector.Apply(
            edit, Options(), null, narrationTiming: Timing(edit), captionGrounding: grounding);

        Assert.Contains(result.Scenes[0].TextOverlays, overlay => overlay.Text.StartsWith("WELCOME", StringComparison.Ordinal));
        Assert.Contains(result.Scenes[1].TextOverlays, overlay => overlay.Text.Contains("KITCHEN", StringComparison.Ordinal));
        Assert.Empty(CaptionSceneGrounding.FindMismatches(result, grounding));
    }

    [Fact]
    public void TranslatedCaptionFollowsThePhotoItsLineWasPlannedOn()
    {
        var (kitchen, dining) = (Guid.NewGuid(), Guid.NewGuid());
        var master = Vertical(
            [Scene(1, 0, 10_000, kitchen, ["narration-01"])],
            [new NarrationSegment("narration-01", 0, 9_000, "La cocina tiene una isla grande.", "story.voiceover.1")]);
        var rooms = new Dictionary<Guid, string?> { [kitchen] = "Kitchen", [dining] = "Dining room" };
        var grounding = CaptionSceneGrounding.From(master, rooms);
        var overDining = master with { Scenes = [Scene(1, 0, 10_000, dining)] };
        var overKitchen = master with { Scenes = [Scene(1, 0, 10_000, kitchen)] };

        var dropped = CampaignCreativeDirector.Apply(
            overDining, Options(), null, narrationTiming: Timing(overDining), captionGrounding: grounding);
        var kept = CampaignCreativeDirector.Apply(
            overKitchen, Options(), null, narrationTiming: Timing(overKitchen), captionGrounding: grounding);

        Assert.DoesNotContain(dropped.Scenes[0].TextOverlays, CampaignVideoEditor.IsCaption);
        Assert.Contains(kept.Scenes[0].TextOverlays, CampaignVideoEditor.IsCaption);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("​ ​")]
    [InlineData("\t\r\n")]
    public void BlankCaptionsNeverBecomeOverlays(string caption)
    {
        var media = Guid.NewGuid();
        var edit = Vertical(
            [Scene(1, 0, 10_000, media)],
            [new NarrationSegment("narration-01", 0, 9_000, "Welcome home.", "story.voiceover.1")]);

        var result = CampaignCreativeDirector.Apply(
            edit, Options(), null,
            new Dictionary<string, string> { ["narration-01"] = caption }, Timing(edit));

        Assert.DoesNotContain(result.Scenes.SelectMany(scene => scene.TextOverlays), CampaignVideoEditor.IsCaption);
    }

    [Fact]
    public void MultilineAndLocalizedCaptionsBecomeSingleLineOverlays()
    {
        var media = Guid.NewGuid();
        var edit = Vertical(
            [Scene(1, 0, 10_000, media)],
            [new NarrationSegment("narration-01", 0, 9_000, "Welcome home.", "story.voiceover.1")]);

        var result = CampaignCreativeDirector.Apply(
            edit, Options(), null,
            new Dictionary<string, string> { ["narration-01"] = "Bienvenue\nà la maison,\tcafé compris." },
            Timing(edit));

        var captions = result.Scenes.SelectMany(scene => scene.TextOverlays).Where(CampaignVideoEditor.IsCaption).ToArray();
        Assert.NotEmpty(captions);
        Assert.All(captions, caption =>
        {
            Assert.False(string.IsNullOrWhiteSpace(caption.Text));
            Assert.DoesNotContain(caption.Text, character => char.IsControl(character));
        });
        Assert.Contains(captions, caption => caption.Text.Contains("CAFÉ", StringComparison.Ordinal));
    }

    [Fact]
    public void FullVoiceoverShotsShowTheRoomBeingDescribed()
    {
        var rooms = new[] { "Exterior front", "Entry / foyer", "Kitchen", "Living room", "Dining room", "Primary bedroom" };
        var ids = rooms.Select(_ => Guid.NewGuid()).ToArray();
        var lines = new[]
        {
            "Welcome to this home on a quiet street.",
            "Step through the front door into a bright foyer.",
            "The kitchen has a large island with seating.",
            "The living room is open and full of light.",
            "The dining room sits right off the kitchen.",
            "Upstairs the primary bedroom feels calm. Contact us today.",
        };
        var sceneMs = 10_000;
        var master = new VideoProductionSpecification(
            "1.0", Guid.NewGuid(), Guid.NewGuid(), 1, RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new FactBinding("property.listingPrice", "$450,000", FactSource.VerifiedProperty, "ListingPrice"),
                new FactBinding("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
                .. lines.Select((line, index) =>
                    new FactBinding($"story.voiceover.{index + 1}", line, FactSource.PropertyStory, "VoiceoverScript")),
            ],
            new BrandKit(null, null, "Demo Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan(
                lines.Select((line, index) => new NarrationSegment(
                    $"narration-{index + 1:00}", index * sceneMs + 300, sceneMs - 600, line,
                    $"story.voiceover.{index + 1}")).ToArray(),
                new MusicPlan("demo", MusicMood.WarmCinematic, 0, 60_000, -24, 500, 500, -18)),
            ids.Select((id, index) => Scene(index + 1, index * sceneMs, sceneMs, id, [$"narration-{index + 1:00}"]))
                .ToArray());
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Voiceover = CampaignVoiceoverMode.Full,
            Pace = CampaignPace.Cinematic,
        };

        var edit = ShortFormEditor.Create(new ShortFormEditRequest(
            master, ids.Select(id => new ShortFormPhoto(id, 1_920, 1_080)).ToArray(), [],
            ShortFormCopyBuilder.DurationFor(options.Voiceover), 500, options.Pace,
            ShortFormCopyBuilder.Build(master, options, "Demo City", "FL")));
        var grounding = CaptionSceneGrounding.From(
            master, ids.Select((id, index) => (id, rooms[index])).ToDictionary(item => item.id, item => (string?)item.Item2));
        var result = CampaignCreativeDirector.Apply(
            edit, options, null,
            master.Audio.NarrationSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
            Timing(edit), grounding);

        // Each middle shot shows the photo of the line being spoken at its midpoint.
        foreach (var scene in edit.Scenes.Skip(1).SkipLast(1))
        {
            var middle = scene.StartMs + scene.DurationMs / 2;
            var spoken = edit.Audio.NarrationSegments.FirstOrDefault(segment =>
                segment.StartMs <= middle && middle < segment.StartMs + segment.DurationMs);
            if (spoken is null)
            {
                continue;
            }

            var index = int.Parse(spoken.Id["narration-".Length..], System.Globalization.CultureInfo.InvariantCulture) - 1;
            Assert.Equal(ids[index], scene.VisualSource.PropertyMediaId);
        }

        Assert.Empty(CaptionSceneGrounding.FindMismatches(result, grounding));
        var captions = result.Scenes.SelectMany(scene => scene.TextOverlays).Where(CampaignVideoEditor.IsCaption).ToArray();
        Assert.Contains(captions, caption => caption.Text.Contains("KITCHEN", StringComparison.Ordinal));
        Assert.Contains(captions, caption => caption.Text.Contains("BEDROOM", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpectationsFollowTheSpecificationOutput()
    {
        var edit = Vertical([Scene(1, 0, 30_000, Guid.NewGuid())], []);

        var expectations = Mp4Expectations.For(edit);

        Assert.Equal((1_080, 1_920, "h264", "aac", 30d), (expectations.Width, expectations.Height,
            expectations.VideoCodec, expectations.AudioCodec, expectations.DurationSeconds));
        Assert.True(expectations.DurationToleranceSeconds is > 0 and < 1);
    }

    private static CampaignCreativeOptions Options() => CampaignCreativeOptions.SocialDefault with
    {
        Soundtrack = CampaignSoundtrack.None,
        Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.BackgroundMusic,
    };

    private static VoiceTimingMetadata Timing(VideoProductionSpecification specification) =>
        new([], specification.Audio.NarrationSegments.Select(segment =>
            new VoiceSegmentTiming(segment.Id, segment.StartMs, segment.StartMs + segment.DurationMs)).ToArray());

    private static VideoScene Scene(int number, int startMs, int durationMs, Guid mediaId, string[]? segments = null) =>
        new(number, startMs, durationMs,
            new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
            new TransitionPlan(TransitionKind.Cut, 0),
            new MotionPlan(MotionKind.None, Full, Full, MotionEasing.Linear),
            [], [], segments ?? []);

    private static VideoProductionSpecification Vertical(VideoScene[] scenes, NarrationSegment[] narration) =>
        new(
            "1.0", Guid.NewGuid(), Guid.NewGuid(), 1,
            scenes.Sum(scene => scene.DurationMs) >= 30_000 ? RequestedDuration.Feature30 : RequestedDuration.Teaser15,
            VideoAspectRatio.Vertical9By16,
            new VideoOutputProfile(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.075m, 0.05m, 0.85m, 0.9m),
            narration.Select(segment => new FactBinding(
                segment.GroundingKey, segment.Text, FactSource.PropertyStory, "VoiceoverScript")).ToArray(),
            new BrandKit(null, null, null, null, null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan(narration, new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            scenes);
}
