using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.TestSupport;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    /// <summary>
    /// Every format's recipe, built into a library video plan, rendered by real FFmpeg and decoded end to end by the
    /// technical validator. Footage beats use real H.264 clips; narrated formats get a tone track timed to the plan.
    /// </summary>
    [Theory]
    [InlineData(VerticalCreativeFormat.WowFeature)]
    [InlineData(VerticalCreativeFormat.PriceFirst)]
    [InlineData(VerticalCreativeFormat.HostedPov)]
    [InlineData(VerticalCreativeFormat.StoryReveal)]
    [InlineData(VerticalCreativeFormat.ExclusiveAccess)]
    public async Task EveryLibraryFormatRendersAndPassesTheTechnicalCheck(VerticalCreativeFormat format)
    {
        if (Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E") != "1")
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable("TIKTOK_REVIEW_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"listingstudio-library-{Guid.NewGuid():N}");
        var directory = Path.Combine(Path.GetFullPath(root), $"library-{format}");
        Directory.CreateDirectory(directory);
        var options = Options.Create(new FfmpegOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
            ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
            RenderTimeoutSeconds = 900,
        });

        var input = RecipeFixture.Rich() with
        {
            AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private showings this weekend only"),
        };
        var recipe = VerticalCreativePlan.Create(input).Recipes.Single(candidate => candidate.Format == format);
        var (master, _, _, _) = await CreateRoomTourAsync(directory);
        var assets = new List<VideoRenderMediaAsset>();
        var colors = new (byte R, byte G, byte B)[] { (40, 110, 60), (200, 190, 160), (230, 230, 235), (180, 140, 100), (120, 60, 50), (90, 110, 160) };
        foreach (var (photo, index) in input.Photos.Select((photo, index) => (photo, index)))
        {
            var path = Path.Combine(directory, $"photo-{index + 1}.ppm");
            var color = colors[index % colors.Length];
            await WriteSolidImageAsync(path, color.R, color.G, color.B);
            assets.Add(new VideoRenderMediaAsset(photo.MediaId, path, 320, 180));
        }

        var videos = new List<VideoRenderPropertyVideoAsset>();
        foreach (var (footage, index) in input.Footage.Select((footage, index) => (footage, index)))
        {
            var path = Path.Combine(directory, $"footage-{index + 1}.mp4");
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-f", "lavfi",
                "-i", $"testsrc=size=640x360:rate=30:duration={footage.DurationMs / 1000.0:0.###}",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast", path);
            videos.Add(new VideoRenderPropertyVideoAsset(footage.VideoId, path, 640, 360, footage.DurationMs));
        }

        var specification = VerticalVariantSpecificationBuilder.Build(
            master,
            recipe,
            assets.Select(asset => new VerticalVariantPhotoSize(asset.PropertyMediaId, asset.Width, asset.Height)).ToArray(),
            videos.Select(video => new VerticalVariantVideoSize(video.PropertyVideoId, video.Width, video.Height, video.DurationMs)).ToArray());
        var creative = VerticalVariantSpecificationBuilder.OptionsFor(CampaignCreativeOptions.SocialDefault, recipe);
        var timing = new VoiceTimingMetadata([], specification.Audio.NarrationSegments.Select(segment =>
            new VoiceSegmentTiming(segment.Id, segment.StartMs, segment.StartMs + segment.DurationMs)).ToArray());
        var rooms = input.Photos.ToDictionary(photo => photo.MediaId, photo => photo.Stop);
        var grounding = CaptionSceneGrounding.From(specification, rooms);
        specification = CampaignCreativeDirector.Apply(
            specification, creative, 1998,
            specification.Audio.NarrationSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
            timing, grounding);
        Assert.Empty(CaptionSceneGrounding.FindMismatches(specification, grounding));

        var seconds = recipe.DurationSeconds;
        var narrationPath = Path.Combine(directory, "test-tone.wav");
        var musicPath = Path.Combine(directory, "test-music.wav");
        await WriteSampleAudioAsync(narrationPath, 440, seconds, 0.15);
        await WriteSampleAudioAsync(musicPath, 220, seconds, 0.03);
        var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions
        {
            FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" : string.Empty,
        }));
        var output = Path.Combine(directory, "video.mp4");
        await renderer.RenderAsync(new VideoRenderRequest(
            specification,
            assets,
            specification.Audio.NarrationSegments.Count == 0 ? null : new VideoRenderNarrationAsset(narrationPath, timing),
            [],
            new VideoRenderMusicAsset(specification.Audio.Music.AssetId ?? "demo-music", musicPath),
            output,
            PropertyVideos: videos));

        var validator = new FfmpegMp4TechnicalValidator(options);
        var result = await validator.ValidateAsync(output, Mp4Expectations.For(specification), CancellationToken.None);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal((1_080, 1_920), (result.Width, result.Height));
        Assert.InRange(result.DurationSeconds!.Value, seconds - 0.2, seconds + 0.2);
        await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-i", output,
            "-vf", $"fps=6/{seconds},scale=180:320,tile=6x1", "-frames:v", "1",
            Path.Combine(directory, "contact-sheet.jpg"));
    }
}
