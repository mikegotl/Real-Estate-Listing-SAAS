using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Rendering;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class MultilineFullNarrationCaptionTests
{
    [Theory]
    [InlineData("A bright\nkitchen opens into generous living space.")]
    [InlineData("A bright\r\nkitchen\t opens into generous living space.")]
    [InlineData("A bright\u00a0kitchen\u2028opens into generous living space.")]
    public void FullCinematicCaptionsNormalizeWhitespaceAndKeepWordTiming(string spokenText)
    {
        var mediaId = Guid.NewGuid();
        var master = Master(mediaId, spokenText);
        var options = new CampaignCreativeOptions(
            CampaignVideoElement.NarrationCaptions | CampaignVideoElement.ClosingCallToAction,
            CampaignSoundtrack.None, CampaignCaptionStyle.Pop,
            Pace: CampaignPace.Cinematic, Voiceover: CampaignVoiceoverMode.Full);
        var timing = new VoiceTimingMetadata(
            spokenText.Select((character, index) => new VoiceCharacterTiming(
                character.ToString(), index * 40, (index + 1) * 40)).ToArray(),
            [new VoiceSegmentTiming("full", 0, spokenText.Length * 40)]);
        var edit = ShortFormEditor.Create(new ShortFormEditRequest(
            master, [new ShortFormPhoto(mediaId, 1600, 900)], [],
            RequestedDuration.Hero60, 500, CampaignPace.Cinematic,
            ShortFormCopyBuilder.Build(master, options, "Demo City", "FL")));
        var specification = CampaignCreativeDirector.Apply(edit, options, 2020, narrationTiming: timing);
        var captions = specification.Scenes
            .SelectMany(scene => scene.TextOverlays
                .Where(overlay => overlay.StyleToken == TextOverlayStyle.PopCaption)
                .Select(overlay => (StartMs: scene.StartMs + overlay.StartOffsetMs, Overlay: overlay)))
            .OrderBy(item => item.StartMs)
            .ToArray();

        Assert.NotEmpty(captions);
        Assert.Equal(
            string.Join(' ', spokenText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant(),
            string.Join(' ', captions.Select(item => item.Overlay.Text)));
        Assert.All(captions, item => Assert.DoesNotContain(item.Overlay.Text, char.IsControl));
        Assert.Equal(master.Audio.NarrationSegments, specification.Audio.NarrationSegments);
        // Normalizing display whitespace must not discard the provider's per-word timing.
        foreach (var caption in captions)
        {
            var firstWord = caption.Overlay.Text.Split(' ')[0];
            var originalIndex = spokenText.IndexOf(firstWord, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(500 + originalIndex * 40, caption.StartMs);
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-caption-whitespace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var photo = Path.Combine(directory, "photo.jpg");
            var audio = Path.Combine(directory, "voice.wav");
            // Command construction checks asset identity/existence; it does not decode bytes or invoke FFmpeg.
            File.WriteAllBytes(photo, [1]);
            File.WriteAllBytes(audio, [1]);
            var command = FfmpegCommandBuilder.Build(new VideoRenderRequest(
                specification, [new VideoRenderMediaAsset(mediaId, photo, 1600, 900)],
                new VideoRenderNarrationAsset(audio, timing), [], null,
                Path.Combine(directory, "video.mp4")));
            Assert.Contains("-filter_complex", command.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VideoProductionSpecification Master(Guid mediaId, string spokenText) => new(
        "1.0", Guid.NewGuid(), Guid.NewGuid(), 1, RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9,
        new VideoOutputProfile(1920, 1080, 30, "h264", "aac", "yuv420p", 48000, 2),
        new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
        [
            new FactBinding("story.voiceover", spokenText, FactSource.PropertyStory, "VoiceoverScript"),
            new FactBinding("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
        ],
        new BrandKit(null, null, null, null, null, null, "#17324D", "#F4F0E8"),
        new GroundedText("Contact the listing team.", "story.closingCta"),
        new AudioPlan(
            [new NarrationSegment("full", 500, 20000, spokenText, "story.voiceover")],
            new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
        [new VideoScene(
            1, 0, 60000, new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
            new TransitionPlan(TransitionKind.Cut, 0),
            new MotionPlan(MotionKind.None, new NormalizedRect(0, 0, 1, 1), new NormalizedRect(0, 0, 1, 1), MotionEasing.Linear),
            [], [], ["full"])]);
}
