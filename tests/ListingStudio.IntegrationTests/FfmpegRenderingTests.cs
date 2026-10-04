using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class FfmpegRenderingTests
{
    [Fact]
    public async Task RendersPlayable1080pH264AacSampleCampaign()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var imagePath = Path.Combine(directory, "sample-property.ppm");
            var narrationPath = Path.Combine(directory, "sample-narration.wav");
            var musicPath = Path.Combine(directory, "sample-music.wav");
            var agentLogoPath = Path.Combine(directory, "agent-logo.ppm");
            var brokerageLogoPath = Path.Combine(directory, "brokerage-logo.ppm");
            var outputPath = Path.Combine(directory, "sample-campaign.mp4");
            await WriteSampleImageAsync(imagePath);
            await WriteSampleNarrationAsync(narrationPath);
            await WriteSampleMusicAsync(musicPath);
            await WriteSolidImageAsync(agentLogoPath, 160, 160, 244, 240, 232);
            await WriteSolidImageAsync(brokerageLogoPath, 240, 120, 23, 50, 77);
            var mediaId = Guid.NewGuid();
            var request = new VideoRenderRequest(
                CreateSpecification(mediaId),
                [new VideoRenderMediaAsset(mediaId, imagePath, 320, 180)],
                new VideoRenderNarrationAsset(
                    narrationPath,
                    new VoiceTimingMetadata(
                        [],
                        [new VoiceSegmentTiming("narration-1", 0, 1_000)])),
                [
                    new VideoRenderBrandAsset("agent-logo", agentLogoPath, 160, 160),
                    new VideoRenderBrandAsset("brokerage-logo", brokerageLogoPath, 240, 120),
                ],
                new VideoRenderMusicAsset("licensed-music", musicPath),
                outputPath);
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
                RenderTimeoutSeconds = 180,
            });
            var renderer = new FfmpegVideoRenderer(
                options,
                Options.Create(new VideoBrandingTemplateOptions
                {
                    FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                        ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
                        : string.Empty,
                }));

            var result = await renderer.RenderAsync(request);

            Assert.Equal(0, result.ExitCode);
            Assert.True(new FileInfo(outputPath).Length > 1_000);
            using var probe = await ProbeAsync(options.Value.ProbeExecutablePath, outputPath);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
            Assert.Equal("h264", video.GetProperty("codec_name").GetString());
            Assert.Equal(1_920, video.GetProperty("width").GetInt32());
            Assert.Equal(1_080, video.GetProperty("height").GetInt32());
            Assert.Equal("30/1", video.GetProperty("r_frame_rate").GetString());
            Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
            var duration = decimal.Parse(
                probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 14.9m, 15.1m);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VideoProductionSpecification CreateSpecification(Guid mediaId)
    {
        var viewport = new NormalizedRect(0, 0, 1, 1);
        return new VideoProductionSpecification(
            "1.0",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            RequestedDuration.Teaser15,
            VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new FactBinding(
                    "property.address.full", "123 Main Street, Raleigh, NC 27601",
                    FactSource.VerifiedProperty, "Address1+City+State+ZipCode"),
                new FactBinding(
                    "property.listingPrice", "$750,000", FactSource.VerifiedProperty, "ListingPrice"),
                new FactBinding(
                    "property.bedBathSquareFeet", "4 beds • 3 baths • 2,600 sq ft",
                    FactSource.VerifiedProperty, "Bedrooms+Bathrooms+SquareFeet"),
                new FactBinding("brand.agent", "Avery Agent • 555-0100", FactSource.BrandKit, "BrandKit"),
                new FactBinding(
                    "story.closingCta", "Contact the listing team.",
                    FactSource.PropertyStory, "ClosingCta"),
            ],
            new BrandKit(
                "agent-logo", "brokerage-logo", "Avery Agent", "555-0100",
                "avery@example.test", "example.test", "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan(
                [new NarrationSegment("narration-1", 500, 2_000, "Welcome home.", "story.voiceover")],
                new MusicPlan(
                    "licensed-music", MusicMood.WarmCinematic, 0, 15_000, -24, 1_000, 1_000, -18)),
            [
                new VideoScene(
                    1,
                    0,
                    7_500,
                    new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(
                        MotionKind.KenBurns,
                        viewport,
                        new NormalizedRect(0.04m, 0.04m, 0.92m, 0.92m),
                        MotionEasing.EaseInOut),
                    [
                        new TextOverlay(
                            "opening-address",
                            "123 Main Street, Raleigh, NC 27601",
                            "property.address.full",
                            500,
                            2_250,
                            OverlayAnchor.TopCenter,
                            new NormalizedRect(0.12m, 0.08m, 0.76m, 0.14m),
                            TextOverlayStyle.OpeningTitle),
                        new TextOverlay(
                            "price",
                            "$750,000",
                            "property.listingPrice",
                            3_000,
                            1_750,
                            OverlayAnchor.BottomLeft,
                            new NormalizedRect(0.08m, 0.75m, 0.3m, 0.1m),
                            TextOverlayStyle.PropertyFact),
                    ],
                    [
                        new LogoOverlay(
                            "agent-logo", 500, 6_000,
                            new NormalizedRect(0.78m, 0.08m, 0.12m, 0.12m), 0.9m),
                    ],
                    ["narration-1"]),
                new VideoScene(
                    2,
                    7_500,
                    7_500,
                    new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Crossfade, 500),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [
                        new TextOverlay(
                            "facts",
                            "4 beds • 3 baths • 2,600 sq ft",
                            "property.bedBathSquareFeet",
                            500,
                            2_000,
                            OverlayAnchor.BottomLeft,
                            new NormalizedRect(0.08m, 0.75m, 0.55m, 0.1m),
                            TextOverlayStyle.LowerThird),
                        new TextOverlay(
                            "agent-contact",
                            "Avery Agent • 555-0100",
                            "brand.agent",
                            3_000,
                            1_500,
                            OverlayAnchor.BottomLeft,
                            new NormalizedRect(0.08m, 0.75m, 0.5m, 0.1m),
                            TextOverlayStyle.LowerThird),
                        new TextOverlay(
                            "closing-cta",
                            "Contact the listing team.",
                            "story.closingCta",
                            4_500,
                            2_000,
                            OverlayAnchor.BottomCenter,
                            new NormalizedRect(0.2m, 0.78m, 0.6m, 0.1m),
                            TextOverlayStyle.ClosingCta),
                    ],
                    [
                        new LogoOverlay(
                            "brokerage-logo", 3_000, 4_000,
                            new NormalizedRect(0.75m, 0.08m, 0.15m, 0.12m), 1m),
                    ],
                    []),
            ]);
    }

    private static async Task<JsonDocument> ProbeAsync(string executable, string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "stream=codec_type,codec_name,width,height,r_frame_rate",
            "-show_entries", "format=duration",
            "-of", "json",
            outputPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffprobe did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        return JsonDocument.Parse(output);
    }

    private static async Task WriteSampleImageAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("P6\n320 180\n255\n"));
        var row = new byte[320 * 3];
        for (var x = 0; x < 320; x++)
        {
            row[x * 3] = (byte)(30 + x * 180 / 319);
            row[x * 3 + 1] = (byte)(70 + x * 80 / 319);
            row[x * 3 + 2] = (byte)(140 + x * 80 / 319);
        }

        for (var y = 0; y < 180; y++)
        {
            await stream.WriteAsync(row);
        }
    }

    private static async Task WriteSampleNarrationAsync(string path)
        => await WriteSampleAudioAsync(path, 220, 1, 0.1);

    private static async Task WriteSampleMusicAsync(string path)
        => await WriteSampleAudioAsync(path, 330, 2, 0.04);

    private static async Task WriteSampleAudioAsync(string path, int frequencyHz, int durationSeconds, double gain)
    {
        const int sampleRate = 48_000;
        const int channels = 2;
        const int bitsPerSample = 16;
        var sampleCount = sampleRate * durationSeconds;
        var dataLength = sampleCount * channels * bitsPerSample / 8;
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * index / sampleRate) * short.MaxValue * gain);
            writer.Write(sample);
            writer.Write(sample);
        }

        await stream.FlushAsync();
    }

    private static async Task WriteSolidImageAsync(
        string path,
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        var row = new byte[width * 3];
        for (var x = 0; x < width; x++)
        {
            row[x * 3] = red;
            row[x * 3 + 1] = green;
            row[x * 3 + 2] = blue;
        }

        for (var y = 0; y < height; y++)
        {
            await stream.WriteAsync(row);
        }
    }
}
