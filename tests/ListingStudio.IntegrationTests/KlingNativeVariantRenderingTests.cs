using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.TestSupport;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    [Theory]
    [InlineData(VerticalCreativeFormat.WowFeature, null)]
    [InlineData(VerticalCreativeFormat.PriceFirst, null)]
    [InlineData(VerticalCreativeFormat.PriceFirst, NativePresenterVideoPolicy.PilotDurationSeconds)]
    [InlineData(VerticalCreativeFormat.HostedPov, null)]
    [InlineData(VerticalCreativeFormat.HostedPov, NativePresenterVideoPolicy.PilotDurationSeconds)]
    public async Task KlingNativePresenterLibraryVariantRendersWithKlingSound(VerticalCreativeFormat format, int? pilotSeconds)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kling-native-variant-{format}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = Options.Create(new FfmpegOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
            ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
            RenderTimeoutSeconds = 900,
        });
        var input = RecipeFixture.Rich();
        var recipe = VerticalCreativePlan.Create(input).Recipes.Single(candidate => candidate.Format == format);
        if (pilotSeconds is { } shorter)
        {
            // Pilot mode plans every video at the 15-second Kling length.
            recipe = recipe.RetimedTo(shorter);
        }
        var (master, _, _, _) = await CreateRoomTourAsync(directory);
        var assets = new List<VideoRenderMediaAsset>();
        foreach (var (photo, index) in input.Photos.Select((photo, index) => (photo, index)))
        {
            var path = Path.Combine(directory, $"photo-{index + 1}.ppm");
            await WriteSolidImageAsync(path, 120, 110, 100);
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
        var timing = new Application.Audio.VoiceTimingMetadata([], specification.Audio.NarrationSegments.Select(segment =>
            new Application.Audio.VoiceSegmentTiming(segment.Id, segment.StartMs, segment.StartMs + segment.DurationMs)).ToArray());
        var grounding = CaptionSceneGrounding.From(specification, input.Photos.ToDictionary(photo => photo.MediaId, photo => photo.Stop));
        specification = CampaignCreativeDirector.Apply(
            specification, creative, 1998,
            specification.Audio.NarrationSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
            timing, grounding);
        specification = CampaignGenerationProcessor.UseNativeKlingAudio(specification);

        // What Kling returns for this video: a 9:16 clip of the video's own length with its own speech and music.
        var seconds = recipe.DurationSeconds;
        var kling = Path.Combine(directory, "kling4-native-presenter.mp4");
        await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error",
            "-f", "lavfi", "-i", $"testsrc=size=1080x1920:rate=24:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=330:duration={seconds}",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", kling);
        var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions
        {
            FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" : string.Empty,
        }));
        var output = Path.Combine(directory, "video.mp4");
        await renderer.RenderAsync(new VideoRenderRequest(
            specification,
            assets,
            null,
            [],
            null,
            output,
            PropertyVideos: videos,
            FullTour: new VideoRenderFullTourAsset(kling, seconds * 1_000, UseNativeAudio: true)));

        var result = await new FfmpegMp4TechnicalValidator(options)
            .ValidateAsync(output, Mp4Expectations.For(specification), CancellationToken.None);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.InRange(result.DurationSeconds!.Value, seconds - 0.2, seconds + 0.2);
    }
}
