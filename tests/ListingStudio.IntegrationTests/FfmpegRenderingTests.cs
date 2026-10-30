using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Processing;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    private static readonly string[] SetupArguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-n"];

    [Fact]
    public async Task GeneratesAndRendersCompleteCampaignDerivativeSet()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-derivatives-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var imagePath = Path.Combine(directory, "campaign-property.ppm");
            var generatedClipPath = Path.Combine(directory, "generated-motion.mp4");
            await WriteSampleImageAsync(imagePath);
            var mediaId = Guid.NewGuid();
            var derivatives = new CampaignDerivativeGenerator().Generate(new CampaignDerivativeRequest(
                CreateMasterSpecification(mediaId),
                [new CampaignDerivativeMedia(mediaId, 320, 180)]));
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
                RenderTimeoutSeconds = 180,
            });
            var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions
            {
                FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                    ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
                    : string.Empty,
            }));
            await WriteGeneratedClipAsync(options.Value.ExecutablePath, imagePath, generatedClipPath);
            var selected = derivatives.Derivatives.Where(item =>
                item.AspectRatio == VideoAspectRatio.Landscape16By9
                || item.Kind == CampaignDeliverableKind.Teaser).ToArray();

            Assert.Equal(4, selected.Length);
            foreach (var derivative in selected)
            {
                var specification = derivative.Specification;
                IReadOnlyList<VideoRenderGeneratedClipAsset> generatedClips = [];
                if (derivative.Kind == CampaignDeliverableKind.Teaser
                    && derivative.AspectRatio == VideoAspectRatio.Landscape16By9)
                {
                    var clipId = Guid.NewGuid();
                    var scenes = specification.Scenes.ToArray();
                    scenes[0] = scenes[0] with
                    {
                        VisualSource = new VisualSource(
                            VisualSourceKind.GeneratedClip,
                            null,
                            clipId,
                            mediaId,
                            null),
                        Motion = scenes[0].Motion with
                        {
                            Type = MotionKind.None,
                            EndViewport = scenes[0].Motion.StartViewport,
                        },
                    };
                    specification = specification with { Scenes = scenes };
                    generatedClips =
                    [
                        new VideoRenderGeneratedClipAsset(clipId, generatedClipPath, 320, 180, 8_000),
                    ];
                }

                var outputPath = Path.Combine(
                    directory,
                    $"{derivative.Kind}-{derivative.AspectRatio}.mp4");
                var result = await renderer.RenderAsync(new VideoRenderRequest(
                    specification,
                    [new VideoRenderMediaAsset(mediaId, imagePath, 320, 180)],
                    null,
                    [],
                    null,
                    outputPath,
                    generatedClips));

                Assert.Equal(0, result.ExitCode);
                Assert.True(new FileInfo(outputPath).Length > 1_000);
                using var probe = await ProbeAsync(options.Value.ProbeExecutablePath, outputPath);
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
                var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
                Assert.Equal("h264", video.GetProperty("codec_name").GetString());
                Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
                Assert.Equal(derivative.Specification.Output.Width, video.GetProperty("width").GetInt32());
                Assert.Equal(derivative.Specification.Output.Height, video.GetProperty("height").GetInt32());
                var duration = decimal.Parse(
                    probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(
                    duration,
                    (decimal)(int)derivative.Specification.RequestedDuration - 0.1m,
                    (decimal)(int)derivative.Specification.RequestedDuration + 0.1m);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    [Fact]
    public async Task RendersAWalkthroughClipThatLandsOnTheNextPhoto()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-walkthrough-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var frontPath = Path.Combine(directory, "front.ppm");
            var foyerPath = Path.Combine(directory, "foyer.ppm");
            var clipPath = Path.Combine(directory, "walkthrough.mp4");
            var outputPath = Path.Combine(directory, "walkthrough-campaign.mp4");
            await WriteSolidImageAsync(frontPath, 320, 180, 30, 90, 200);
            await WriteSolidImageAsync(foyerPath, 320, 180, 200, 40, 40);
            var executable = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
            await RunFfmpegAsync(
                executable,
                "-loop", "1", "-framerate", "24", "-t", "5", "-i", frontPath,
                "-loop", "1", "-framerate", "24", "-t", "5", "-i", foyerPath,
                "-filter_complex", "[0:v][1:v]xfade=transition=fade:duration=4:offset=0.5,scale=640:360,format=yuv420p",
                "-t", "5", "-c:v", "libx264", "-an", clipPath);
            var frontId = Guid.NewGuid();
            var foyerId = Guid.NewGuid();
            var viewport = new NormalizedRect(0, 0, 1, 1);
            var specification = CreateMasterSpecification(frontId) with
            {
                RequestedDuration = RequestedDuration.Teaser15,
                FactBindings = [],
                Audio = new AudioPlan([], new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
                Scenes =
                [
                    new VideoScene(
                        1, 0, 7_000,
                        new VisualSource(VisualSourceKind.PropertyMedia, frontId, null, null, null),
                        new TransitionPlan(TransitionKind.Cut, 0),
                        new MotionPlan(
                            MotionKind.KenBurns, viewport, new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
                            MotionEasing.EaseInOut),
                        [], [], []),
                    new VideoScene(
                        2, 7_000, 8_000,
                        new VisualSource(VisualSourceKind.PropertyMedia, foyerId, null, null, null),
                        new TransitionPlan(TransitionKind.Crossfade, 500),
                        new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                        [], [], []),
                ],
            };
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = executable,
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
                RenderTimeoutSeconds = 180,
            });
            var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions()));

            var result = await renderer.RenderAsync(new VideoRenderRequest(
                specification,
                [
                    new VideoRenderMediaAsset(frontId, frontPath, 320, 180),
                    new VideoRenderMediaAsset(foyerId, foyerPath, 320, 180),
                ],
                null,
                [],
                null,
                outputPath,
                WalkthroughTransitions:
                [
                    new VideoRenderWalkthroughTransitionAsset(frontId, foyerId, clipPath, 5_000),
                ]));

            Assert.Equal(0, result.ExitCode);
            using var probe = await ProbeAsync(options.Value.ProbeExecutablePath, outputPath);
            var duration = decimal.Parse(
                probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 14.9m, 15.1m);
            // The front photo holds for two seconds, the walk-through plays, and it arrives on the foyer at seven.
            AssertColorNear(await SamplePixelAsync(executable, outputPath, 1.0), 30, 90, 200);
            AssertColorNear(await SamplePixelAsync(executable, outputPath, 6.9), 200, 40, 40);
            AssertColorNear(await SamplePixelAsync(executable, outputPath, 10.0), 200, 40, 40);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DroneIntroIsPlayedBackwardsLabeledAndOpensTheVideoOnTheFrontPhoto()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-aerial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // The provider's pull-back starts on the front photo (green) and climbs away to the sky (blue).
            var frontPath = Path.Combine(directory, "front.ppm");
            var skyPath = Path.Combine(directory, "sky.ppm");
            var pullBackPath = Path.Combine(directory, "pull-back.mp4");
            var outputPath = Path.Combine(directory, "aerial-campaign.mp4");
            await WriteSolidImageAsync(frontPath, 320, 180, 40, 170, 60);
            await WriteSolidImageAsync(skyPath, 320, 180, 40, 70, 210);
            var executable = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
            var probeExecutable = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
            await RunFfmpegAsync(
                executable,
                "-loop", "1", "-framerate", "24", "-t", "5", "-i", frontPath,
                "-loop", "1", "-framerate", "24", "-t", "5", "-i", skyPath,
                "-filter_complex", "[0:v][1:v]xfade=transition=fade:duration=4:offset=0.5,scale=640:360,format=yuv420p",
                "-t", "5", "-c:v", "libx264", "-an", pullBackPath);
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = executable,
                ProbeExecutablePath = probeExecutable,
                RenderTimeoutSeconds = 180,
            });

            var editor = new FfmpegAerialIntroVideoEditor(options);
            AerialIntroEditResult edited;
            await using (var pullBack = File.OpenRead(pullBackPath))
            {
                edited = await editor.PrepareAsync(pullBack);
            }

            var introPath = Path.Combine(directory, "intro.mp4");
            await using (edited.Content)
            await using (var target = File.Create(introPath))
            {
                await edited.Content.CopyToAsync(target);
            }

            Assert.InRange(edited.DurationMs, 4_900, 5_100);
            Assert.Equal(new FileInfo(introPath).Length, edited.FileSize);
            // Reversed: it opens on the sky and descends onto the front photo.
            AssertColorNear(await SamplePixelAsync(executable, introPath, 0.1), 40, 70, 210);
            AssertColorNear(await SamplePixelAsync(executable, introPath, 4.9), 40, 170, 60);

            var frontId = Guid.NewGuid();
            var viewport = new NormalizedRect(0, 0, 1, 1);
            var specification = CreateMasterSpecification(frontId) with
            {
                RequestedDuration = RequestedDuration.Teaser15,
                FactBindings = [],
                Audio = new AudioPlan([], new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
                Scenes =
                [
                    new VideoScene(
                        1, 0, 15_000,
                        new VisualSource(VisualSourceKind.PropertyMedia, frontId, null, null, null),
                        new TransitionPlan(TransitionKind.Cut, 0),
                        new MotionPlan(
                            MotionKind.KenBurns, viewport, new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
                            MotionEasing.EaseInOut),
                        [], [], []),
                ],
            };
            var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions()));

            var result = await renderer.RenderAsync(new VideoRenderRequest(
                specification,
                [new VideoRenderMediaAsset(frontId, frontPath, 320, 180)],
                null,
                [],
                null,
                outputPath,
                AerialIntro: new VideoRenderAerialIntroAsset(frontId, introPath, edited.DurationMs)));

            Assert.Equal(0, result.ExitCode);
            using var probe = await ProbeAsync(probeExecutable, outputPath);
            var duration = decimal.Parse(
                probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 14.9m, 15.1m);
            AssertColorNear(await SamplePixelAsync(executable, outputPath, 0.6), 40, 70, 210);
            AssertColorNear(await SamplePixelAsync(executable, outputPath, 8.0), 40, 170, 60);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertColorNear(byte[] pixel, int red, int green, int blue)
    {
        Assert.True(
            Math.Abs(pixel[0] - red) <= 20 && Math.Abs(pixel[1] - green) <= 20 && Math.Abs(pixel[2] - blue) <= 20,
            $"Expected about ({red}, {green}, {blue}) but sampled ({pixel[0]}, {pixel[1]}, {pixel[2]}).");
    }

    private static async Task<byte[]> SamplePixelAsync(string executable, string videoPath, double seconds)
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
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", videoPath,
            "-frames:v", "1", "-vf", "crop=200:200,scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg did not start for frame sampling.");
        using var output = new MemoryStream();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errorTask);
        return output.ToArray();
    }

    private static async Task RunFfmpegAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in SetupArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg did not start for test setup.");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errorTask);
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

    private static VideoProductionSpecification CreateMasterSpecification(Guid mediaId)
    {
        var viewport = new NormalizedRect(0, 0, 1, 1);
        var scenes = new[]
        {
            new VideoScene(
                1,
                0,
                30_000,
                new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                new TransitionPlan(TransitionKind.Cut, 0),
                new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                [],
                [],
                []),
            new VideoScene(
                2,
                30_000,
                30_000,
                new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                new TransitionPlan(TransitionKind.Crossfade, 500),
                new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                [new TextOverlay(
                    "closing-cta",
                    "Contact the listing team.",
                    "story.closingCta",
                    24_000,
                    5_000,
                    OverlayAnchor.BottomCenter,
                    new NormalizedRect(0.2m, 0.78m, 0.6m, 0.1m),
                    TextOverlayStyle.ClosingCta)],
                [],
                []),
        };
        return new VideoProductionSpecification(
            "1.0",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [new FactBinding(
                "story.closingCta",
                "Contact the listing team.",
                FactSource.PropertyStory,
                "ClosingCta")],
            new BrandKit(null, null, null, null, null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan([], new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            scenes);
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

    private static async Task WriteGeneratedClipAsync(
        string executable,
        string imagePath,
        string outputPath)
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
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-loop", "1", "-framerate", "30", "-t", "8", "-i", imagePath,
            "-vf", "scale=320:180", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-an", outputPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg did not start for generated-clip setup.");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errorTask);
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

