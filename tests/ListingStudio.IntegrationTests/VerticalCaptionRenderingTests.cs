using System.Text;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    private static readonly (string Room, string Line, byte R, byte G, byte B)[] TourStops =
    [
        ("Exterior front", "Welcome to this home on a quiet tree lined street.", 40, 110, 60),
        ("Entry / foyer", "Step through the front door into a bright foyer.", 200, 190, 160),
        ("Kitchen", "The kitchen has a large island with seating for four.", 230, 230, 235),
        ("Living room", "The living room is open and filled with natural light.", 180, 140, 100),
        ("Dining room", "The dining room sits just off the kitchen for easy hosting.", 120, 60, 50),
        ("Primary bedroom", "Upstairs the primary bedroom feels calm and private.", 90, 110, 160),
        ("Bathroom", "The bathroom has a double vanity and a walk in shower.", 220, 240, 250),
        ("Back patio", "Out back the patio is ready for summer evenings. Contact the listing team today.", 60, 150, 90),
    ];

    /// <summary>
    /// A realistic vertical campaign: eight storyboard rooms, one narration line per room in the master plan, and the
    /// creative director's captions burned in. The original three-case suite skips the director, so captions never
    /// reached FFmpeg there.
    /// </summary>
    [Theory]
    [InlineData("full-voiceover-cinematic-60", CampaignVoiceoverMode.Full, CampaignPace.Cinematic, 60)]
    [InlineData("music-text-fast-30", CampaignVoiceoverMode.None, CampaignPace.Fast, 30)]
    public async Task VerticalCampaignWithRoomCaptionsRendersAndValidates(
        string caseId, CampaignVoiceoverMode voiceover, CampaignPace pace, int seconds)
    {
        if (Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E") != "1")
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable("TIKTOK_REVIEW_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"listingstudio-vertical-{Guid.NewGuid():N}");
        var directory = Path.Combine(Path.GetFullPath(root), caseId);
        Directory.CreateDirectory(directory);

        var (master, photos, assets, rooms) = await CreateRoomTourAsync(directory);
        var creative = CampaignCreativeOptions.SocialDefault with { Voiceover = voiceover, Pace = pace };
        var edit = ShortFormEditor.Create(new ShortFormEditRequest(
            master, photos, [], ShortFormCopyBuilder.DurationFor(voiceover), 500, pace,
            ShortFormCopyBuilder.Build(master, creative, "Demo City", "FL")));
        var timing = new VoiceTimingMetadata([], edit.Audio.NarrationSegments.Select(segment =>
            new VoiceSegmentTiming(segment.Id, segment.StartMs, segment.StartMs + segment.DurationMs)).ToArray());
        var grounding = CaptionSceneGrounding.From(master, rooms);
        var specification = CampaignCreativeDirector.Apply(
            edit, creative, 1995,
            master.Audio.NarrationSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
            timing, grounding);
        Assert.Empty(CaptionSceneGrounding.FindMismatches(specification, grounding));
        Assert.All(specification.Scenes.SelectMany(scene => scene.TextOverlays),
            overlay => Assert.False(string.IsNullOrWhiteSpace(overlay.Text)));
        if (voiceover != CampaignVoiceoverMode.None)
        {
            Assert.Contains(specification.Scenes.SelectMany(scene => scene.TextOverlays), CampaignVideoEditor.IsCaption);
        }

        var narrationPath = Path.Combine(directory, "test-tone.wav");
        var musicPath = Path.Combine(directory, "test-music.wav");
        await WriteSampleAudioAsync(narrationPath, 440, seconds, 0.15);
        await WriteSampleAudioAsync(musicPath, 220, seconds, 0.03);
        var options = Options.Create(new FfmpegOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
            ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
            RenderTimeoutSeconds = 900,
        });
        var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions
        {
            FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" : string.Empty,
        }));
        var output = Path.Combine(directory, "video.mp4");
        await renderer.RenderAsync(new VideoRenderRequest(
            specification, assets,
            voiceover == CampaignVoiceoverMode.None ? null : new VideoRenderNarrationAsset(narrationPath, timing),
            [], new VideoRenderMusicAsset(specification.Audio.Music.AssetId ?? "demo-music", musicPath), output));

        var validator = new FfmpegMp4TechnicalValidator(options);
        var result = await validator.ValidateAsync(output, Mp4Expectations.For(specification), CancellationToken.None);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.InRange(result.DurationSeconds!.Value, seconds - 0.2, seconds + 0.2);
        await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-i", output,
            "-vf", $"fps=1/{seconds / 6},scale=180:320,tile=6x1", "-frames:v", "1",
            Path.Combine(directory, "contact-sheet.jpg"));
    }

    private static async Task<(VideoProductionSpecification Master, ShortFormPhoto[] Photos,
        VideoRenderMediaAsset[] Assets, IReadOnlyDictionary<Guid, string?> Rooms)> CreateRoomTourAsync(string directory)
    {
        var viewport = new NormalizedRect(0, 0, 1, 1);
        var ids = TourStops.Select(_ => Guid.NewGuid()).ToArray();
        var assets = new List<VideoRenderMediaAsset>();
        for (var index = 0; index < TourStops.Length; index++)
        {
            var path = Path.Combine(directory, $"room-{index + 1}.ppm");
            await WriteSolidImageAsync(path, TourStops[index].R, TourStops[index].G, TourStops[index].B);
            assets.Add(new VideoRenderMediaAsset(ids[index], path, 320, 180));
        }

        var sceneMs = 60_000 / TourStops.Length;
        var scenes = TourStops.Select((stop, index) => new VideoScene(
            index + 1, index * sceneMs, sceneMs,
            new VisualSource(VisualSourceKind.PropertyMedia, ids[index], null, null, null),
            new TransitionPlan(index == 0 ? TransitionKind.Cut : TransitionKind.Crossfade, index == 0 ? 0 : 400),
            new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
            [], [], [$"narration-{index + 1:00}"])).ToArray();
        var segments = TourStops.Select((stop, index) => new NarrationSegment(
            $"narration-{index + 1:00}", index * sceneMs + 300, sceneMs - 600, stop.Line,
            $"story.voiceover.{index + 1}")).ToArray();
        var bindings = new List<FactBinding>
        {
            new("property.listingPrice", "$450,000", FactSource.VerifiedProperty, "ListingPrice"),
            new("property.bedBath", "3 beds • 2 baths", FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
            new("story.openingHook", "A bright home made for hosting.", FactSource.PropertyStory, "OpeningHook"),
            new("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
        };
        bindings.AddRange(segments.Select(segment =>
            new FactBinding(segment.GroundingKey, segment.Text, FactSource.PropertyStory, "VoiceoverScript")));
        var master = new VideoProductionSpecification(
            "1.0", Guid.NewGuid(), Guid.NewGuid(), 1, RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m), bindings,
            new BrandKit(null, null, "Demo Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan(segments, new MusicPlan("demo-music", MusicMood.WarmCinematic, 0, 60_000, -24, 500, 500, -18)),
            scenes);
        var photos = ids.Select(id => new ShortFormPhoto(id, 320, 180)).ToArray();
        var rooms = ids.Select((id, index) => (id, TourStops[index].Room))
            .ToDictionary(item => item.id, item => (string?)item.Room);
        return (master, photos, assets.ToArray(), rooms);
    }

    private static async Task WriteSolidImageAsync(string path, byte red, byte green, byte blue)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("P6\n320 180\n255\n"));
        var row = new byte[320 * 3];
        for (var x = 0; x < 320; x++)
        {
            row[x * 3] = red;
            row[x * 3 + 1] = green;
            row[x * 3 + 2] = blue;
        }

        for (var y = 0; y < 180; y++)
        {
            await stream.WriteAsync(row);
        }
    }
}
