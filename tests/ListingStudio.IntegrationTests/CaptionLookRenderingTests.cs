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
    public static TheoryData<CampaignCaptionStyle> PickerLooks()
    {
        var data = new TheoryData<CampaignCaptionStyle>();
        foreach (var style in CampaignCaptionStyles.Picker)
        {
            data.Add(style);
        }

        return data;
    }

    /// <summary>
    /// Every caption look on the Campaign tab picker renders with the real FFmpeg renderer and passes the MP4 checks.
    /// With CAPTION_PREVIEW_DIR set, it also writes one still per look; the picker thumbnails in
    /// wwwroot/images/caption-styles are made from those stills, so they show exactly what the renderer draws.
    /// </summary>
    [Theory]
    [MemberData(nameof(PickerLooks))]
    public async Task EveryPickerCaptionLookRendersWithItsBundledFont(CampaignCaptionStyle style)
    {
        if (Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E") != "1")
        {
            return;
        }

        var look = CaptionLooks.For(style);
        if (look is not null)
        {
            Assert.True(File.Exists(look.FontPath), $"Missing bundled font {look.FontFile}");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listingstudio-look-{style}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = Options.Create(new FfmpegOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
            ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
            RenderTimeoutSeconds = 300,
        });

        // The landing page's stock photo (not a listed property), or a plain color when it isn't available.
        var photo = Path.Combine(directory, "photo.jpg");
        var stock = FindStockPhoto();
        if (stock is not null)
        {
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-i", stock, "-vf", "scale=1536:1920,crop=1080:1920", photo);
        }
        else
        {
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-f", "lavfi", "-i", "color=c=0x8a7a66:s=1080x1920",
                "-frames:v", "1", photo);
        }

        var mediaId = Guid.NewGuid();
        var full = new NormalizedRect(0, 0, 1, 1);
        TextOverlay Overlay(string id, string text, string key, TextOverlayStyle token, decimal y, decimal height) =>
            new(id, text, key, 0, 15_000, OverlayAnchor.Center, new NormalizedRect(0.05m, y, 0.9m, height), token);
        var overlays = new[]
        {
            Overlay("hook-title", "$449,900", "property.listingPrice", TextOverlayStyle.HookTitle, 0.09m, 0.11m),
            Overlay("hook-fact", "3 bd · 2 ba · Demo City", "property.bedBath", TextOverlayStyle.HookFact, 0.22m, 0.06m),
            Overlay("caption-1-narration-01-0", "OPEN KITCHEN MADE", "story.voiceover.1", TextOverlayStyle.PopCaption, 0.70m, 0.06m),
            Overlay("caption-1-narration-01-1", "FOR HOSTING", "story.voiceover.1", TextOverlayStyle.PopCaption, 0.765m, 0.06m),
        };
        var specification = new VideoProductionSpecification(
            "1.0", Guid.NewGuid(), Guid.NewGuid(), 1, RequestedDuration.Teaser15, VideoAspectRatio.Vertical9By16,
            new VideoOutputProfile(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new("property.listingPrice", "$449,900", FactSource.VerifiedProperty, "ListingPrice"),
                new("property.bedBath", "3 bd · 2 ba · Demo City", FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
                new("story.voiceover.1", "Open kitchen made for hosting.", FactSource.PropertyStory, "VoiceoverScript"),
                new("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
            ],
            new BrandKit(null, null, "Demo Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan([], new MusicPlan("demo-music", MusicMood.WarmCinematic, 0, 15_000, -24, 500, 500, -18)),
            [
                new VideoScene(1, 0, 15_000, new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0), new MotionPlan(MotionKind.None, full, full, MotionEasing.Linear),
                    overlays, [], []),
            ]);
        var musicPath = Path.Combine(directory, "music.wav");
        await WriteSampleAudioAsync(musicPath, 220, 15, 0.03);
        var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions()));
        var output = Path.Combine(directory, "video.mp4");
        await renderer.RenderAsync(new VideoRenderRequest(
            specification, [new VideoRenderMediaAsset(mediaId, photo, 1_080, 1_920)], null, [],
            new VideoRenderMusicAsset("demo-music", musicPath), output, CaptionStyle: style));

        var result = await new FfmpegMp4TechnicalValidator(options)
            .ValidateAsync(output, Mp4Expectations.For(specification), CancellationToken.None);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));

        if (Environment.GetEnvironmentVariable("CAPTION_PREVIEW_DIR") is { Length: > 0 } previews)
        {
            Directory.CreateDirectory(previews);
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-ss", "2", "-i", output,
                "-frames:v", "1", Path.Combine(previews, $"{style}-{Guid.NewGuid():N}.png"));
        }
    }

    private static string? FindStockPhoto()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ListingStudio.Web", "wwwroot", "images", "hero-home.webp");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
