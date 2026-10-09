using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class FfmpegCommandBuilderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"listing-studio-ffmpeg-{Guid.NewGuid():N}");
    public FfmpegCommandBuilderTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void BuildCreatesTypedInputsTransitionsMotionAndTimedNarration()
    {
        var request = CreateRequest();

        var command = FfmpegCommandBuilder.Build(request);

        Assert.DoesNotContain("-y", command.Arguments);
        Assert.Contains("-n", command.Arguments);
        Assert.Equal(request.OutputFilePath, command.Arguments[^1]);
        Assert.Equal(2, command.Arguments.Count(argument => argument == request.PropertyMedia[0].FilePath));
        Assert.Contains(request.Narration!.FilePath, command.Arguments);
        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("zoompan=", graph, StringComparison.Ordinal);
        Assert.Contains("xfade=transition=fade:duration=0.5:offset=7", graph, StringComparison.Ordinal);
        Assert.Contains("asplit=2", graph, StringComparison.Ordinal);
        Assert.Contains("atrim=start=0:end=0.5", graph, StringComparison.Ordinal);
        Assert.Contains("adelay=500:all=1", graph, StringComparison.Ordinal);
        Assert.Contains("adelay=9000:all=1", graph, StringComparison.Ordinal);
        Assert.Contains("atrim=duration=15", graph, StringComparison.Ordinal);
        Assert.Equal("libx264", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", arguments[arguments.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public void BuildStartsEachNarrationSegmentWithItsSceneAndNeverOverlapsAnOverrun()
    {
        var request = CreateRequest() with
        {
            Narration = new VideoRenderNarrationAsset(
                CreateFile("narration-long.wav"),
                new VoiceTimingMetadata(
                    [],
                    [
                        new VoiceSegmentTiming("narration-1", 0, 9_000),
                        new VoiceSegmentTiming("narration-2", 9_000, 10_000),
                    ])),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("atrim=start=0:end=9,asetpts=PTS-STARTPTS,adelay=500:all=1[narration0]", graph, StringComparison.Ordinal);
        Assert.Contains("atrim=start=9:end=10,asetpts=PTS-STARTPTS,adelay=9500:all=1[narration1]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUsesContinuousPlannedWindowForNarrationWithoutTiming()
    {
        var request = CreateRequest() with
        {
            Narration = new VideoRenderNarrationAsset(CreateFile("narration-untimed.wav"), null),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("atrim=duration=14.5", graph, StringComparison.Ordinal);
        Assert.Contains("adelay=500:all=1[narration0]", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("asplit=2", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsUnsupportedOutputProfile()
    {
        var request = CreateRequest();
        request = request with
        {
            Specification = request.Specification with
            {
                Output = request.Specification.Output with { Width = 1_280, Height = 720 },
            },
        };

        Assert.Throws<NotSupportedException>(() => FfmpegCommandBuilder.Build(request));
    }

    [Fact]
    public void BuildSupportsCanonicalVerticalOutputProfile()
    {
        var request = CreateRequest();
        var verticalViewport = new NormalizedRect(0.341796875m, 0, 0.31640625m, 1);
        request = request with
        {
            Specification = request.Specification with
            {
                AspectRatio = VideoAspectRatio.Vertical9By16,
                Output = request.Specification.Output with { Width = 1_080, Height = 1_920 },
                SafeZone = new NormalizedRect(0.075m, 0.05m, 0.85m, 0.9m),
                Scenes = request.Specification.Scenes.Select(scene => scene with
                {
                    Motion = new MotionPlan(MotionKind.None, verticalViewport, verticalViewport, MotionEasing.Linear),
                }).ToArray(),
            },
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=1080:1920", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreatesTemplateDrivenBrandingLogosMusicDuckingAndFades()
    {
        var request = CreateBrandedRequest();
        var template = new VideoBrandingTemplateOptions
        {
            VideoFadeInMs = 600,
            VideoFadeOutMs = 900,
            OpeningTitle = new TextOverlayTemplateOptions
            {
                FontSize = 72,
                TextColor = BrandColorToken.Secondary,
                BoxColor = BrandColorToken.Primary,
                BoxOpacity = 0.7m,
                Padding = 26,
            },
        };

        var command = FfmpegCommandBuilder.Build(request, template);

        Assert.Contains(request.Music!.FilePath, command.Arguments);
        Assert.Contains(request.BrandAssets[0].FilePath, command.Arguments);
        Assert.Contains(request.BrandAssets[1].FilePath, command.Arguments);
        Assert.Equal(2, command.Arguments.Count(argument => argument == request.BrandAssets[0].FilePath));
        Assert.Contains("-stream_loop", command.Arguments);
        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("xfade=transition=fadeblack", graph, StringComparison.Ordinal);
        Assert.Contains("drawtext=font='Sans'", graph, StringComparison.Ordinal);
        Assert.Contains("text='123 Main Street\\, Raleigh'", graph, StringComparison.Ordinal);
        Assert.Contains("fontcolor=0xF4F0E8:fontsize=72", graph, StringComparison.Ordinal);
        Assert.Contains("boxcolor=0x17324D@0.7", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=x=", graph, StringComparison.Ordinal);
        Assert.Contains("colorchannelmixer=aa=0.9", graph, StringComparison.Ordinal);
        Assert.Contains("volume=-18dB", graph, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=0:d=1", graph, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=14:d=1", graph, StringComparison.Ordinal);
        Assert.Contains("volume='if(gt(between(t,0.5,1.5)+between(t,9,10),0)", graph, StringComparison.Ordinal);
        Assert.Contains("fade=t=in:st=0:d=0.6", graph, StringComparison.Ordinal);
        Assert.Contains("fade=t=out:st=14.1:d=0.9", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsMissingBrandAndMusicAssets()
    {
        var request = CreateBrandedRequest() with { BrandAssets = [], Music = null };

        var exception = Assert.Throws<ArgumentException>(() => FfmpegCommandBuilder.Build(request));

        Assert.Contains("logo asset", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRejectsMissingPlannedMusicAsset()
    {
        var request = CreateBrandedRequest() with { Music = null };

        var exception = Assert.Throws<ArgumentException>(() => FfmpegCommandBuilder.Build(request));

        Assert.Contains("music asset", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUsesResolvedGeneratedClipForExplicitScene()
    {
        var request = CreateRequest();
        var clipId = Guid.NewGuid();
        var clipPath = CreateFile("generated clip.mp4");
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            VisualSource = new VisualSource(
                VisualSourceKind.GeneratedClip,
                null,
                clipId,
                request.PropertyMedia[0].PropertyMediaId,
                null),
        };
        request = request with
        {
            Specification = request.Specification with { Scenes = scenes },
            GeneratedClips = [new VideoRenderGeneratedClipAsset(clipId, clipPath, 1_920, 1_080, 7_000)],
        };

        var command = FfmpegCommandBuilder.Build(request);

        Assert.Contains(clipPath, command.Arguments);
        Assert.Contains("-stream_loop", command.Arguments);
        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=increase", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFallsBackToPropertyImageWhenGeneratedClipIsUnavailable()
    {
        var request = CreateRequest();
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            VisualSource = new VisualSource(
                VisualSourceKind.GenerativeMotionRequest,
                request.PropertyMedia[0].PropertyMediaId,
                null,
                request.PropertyMedia[0].PropertyMediaId,
                "slow cinematic push forward"),
        };
        request = request with { Specification = request.Specification with { Scenes = scenes } };

        var command = FfmpegCommandBuilder.Build(request);

        Assert.Equal(2, command.Arguments.Count(argument => argument == request.PropertyMedia[0].FilePath));
        Assert.DoesNotContain("-stream_loop", command.Arguments);
    }

    [Fact]
    public void BuildUsesTheApprovedWindowFromAnEnhancedPropertyVideoWithoutItsAudio()
    {
        var request = CreateRequest();
        var videoId = Guid.NewGuid();
        var videoPath = CreateFile("enhanced walkthrough.mp4");
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            VisualSource = new VisualSource(
                VisualSourceKind.PropertyVideo,
                null,
                null,
                null,
                null,
                videoId,
                2_000),
            Motion = new MotionPlan(
                MotionKind.None,
                new NormalizedRect(0, 0, 1, 1),
                new NormalizedRect(0, 0, 1, 1),
                MotionEasing.Linear),
        };
        request = request with
        {
            Specification = request.Specification with { Scenes = scenes },
            PropertyVideos = [new VideoRenderPropertyVideoAsset(videoId, videoPath, 1_920, 1_080, 12_000)],
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var videoInput = arguments.IndexOf(videoPath);
        Assert.True(videoInput > 4);
        var seekArgument = arguments.LastIndexOf("-ss", videoInput);
        Assert.True(seekArgument >= 0);
        Assert.Equal("2", arguments[seekArgument + 1]);
        Assert.DoesNotContain("-stream_loop", arguments.Take(videoInput));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:v]scale=1920:1080", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[0:a]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOverlaysLicensedNeighborhoodPhotoWithTimedCredit()
    {
        var request = CreateRequest();
        var insightId = Guid.NewGuid();
        var neighborhoodPhoto = CreateFile("licensed park photo.jpg");
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            TextOverlays =
            [
                new TextOverlay(
                    "nearby-park",
                    "Lake Park • Park • 0.6 miles straight-line distance",
                    "neighborhood.1",
                    1_000,
                    2_000,
                    OverlayAnchor.BottomLeft,
                    new NormalizedRect(0.08m, 0.75m, 0.84m, 0.1m),
                    TextOverlayStyle.LowerThird),
            ],
        };
        request = request with
        {
            Specification = request.Specification with
            {
                FactBindings =
                [
                    new FactBinding(
                        "neighborhood.1",
                        "Lake Park • Park • 0.6 miles straight-line distance",
                        FactSource.ApprovedNeighborhood,
                        "https://maps.example/park",
                        insightId),
                ],
                Scenes = scenes,
            },
            NeighborhoodAssets =
            [
                new VideoRenderNeighborhoodAsset(
                    insightId,
                    neighborhoodPhoto,
                    1_600,
                    900,
                    "Photo © Example Photographer"),
            ],
        };

        var command = FfmpegCommandBuilder.Build(request);

        Assert.Contains(neighborhoodPhoto, command.Arguments);
        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("neighborhoodVisual0", graph, StringComparison.Ordinal);
        Assert.Contains("between(t,0.5,3.5)", graph, StringComparison.Ordinal);
        Assert.Contains("Photo © Example Photographer", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendererCapturesExitCodeAndUsefulDiagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(directory, "fake-ffmpeg.sh");
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\necho renderer-output\necho render failed safely >&2\nexit 23\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var renderer = new FfmpegVideoRenderer(Options.Create(new FfmpegOptions
        {
            ExecutablePath = executable,
            RenderTimeoutSeconds = 10,
        }), Options.Create(new VideoBrandingTemplateOptions()));

        var exception = await Assert.ThrowsAsync<VideoRenderException>(
            () => renderer.RenderAsync(CreateRequest()));

        Assert.Equal(23, exception.ExitCode);
        Assert.Contains("render failed safely", exception.StandardError, StringComparison.Ordinal);
        Assert.Contains("exit code 23", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private VideoRenderRequest CreateRequest()
    {
        var mediaId = Guid.NewGuid();
        var image = CreateFile("property image.ppm");
        var narration = CreateFile("narration.wav");
        var output = Path.Combine(directory, "tour output.mp4");
        var specification = CreateSpecification(mediaId);
        return new VideoRenderRequest(
            specification,
            [new VideoRenderMediaAsset(mediaId, image, 320, 180)],
            new VideoRenderNarrationAsset(
                narration,
                new VoiceTimingMetadata(
                    [],
                    [
                        new VoiceSegmentTiming("narration-1", 0, 500),
                        new VoiceSegmentTiming("narration-2", 500, 1_000),
                    ])),
            [],
            null,
            output);
    }

    private VideoRenderRequest CreateBrandedRequest()
    {
        var request = CreateRequest();
        var agentLogo = CreateFile("agent logo.ppm");
        var brokerageLogo = CreateFile("brokerage logo.ppm");
        var music = CreateFile("licensed music.wav");
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            TextOverlays =
            [
                new TextOverlay(
                    "address", "123 Main Street, Raleigh", "property.address.full", 250, 2_000,
                    OverlayAnchor.TopCenter, new NormalizedRect(0.15m, 0.08m, 0.7m, 0.12m),
                    TextOverlayStyle.OpeningTitle),
                new TextOverlay(
                    "price", "$750,000", "property.listingPrice", 2_500, 1_500,
                    OverlayAnchor.BottomLeft, new NormalizedRect(0.08m, 0.75m, 0.3m, 0.1m),
                    TextOverlayStyle.PropertyFact),
            ],
            LogoOverlays =
            [
                new LogoOverlay(
                    "agent-logo", 250, 6_000, new NormalizedRect(0.78m, 0.08m, 0.12m, 0.12m), 0.9m),
            ],
        };
        scenes[1] = scenes[1] with
        {
            TransitionIn = new TransitionPlan(TransitionKind.DipToBlack, 500),
            TextOverlays =
            [
                new TextOverlay(
                    "facts", "4 beds • 3 baths • 2,600 sq ft", "property.bedBathSquareFeet", 500, 2_000,
                    OverlayAnchor.BottomLeft, new NormalizedRect(0.08m, 0.75m, 0.55m, 0.1m),
                    TextOverlayStyle.LowerThird),
                new TextOverlay(
                    "agent", "Avery Agent • 555-0100", "brand.agent", 3_000, 1_500,
                    OverlayAnchor.BottomLeft, new NormalizedRect(0.08m, 0.75m, 0.5m, 0.1m),
                    TextOverlayStyle.LowerThird),
                new TextOverlay(
                    "cta", "Call today", "cta", 5_000, 2_000,
                    OverlayAnchor.BottomCenter, new NormalizedRect(0.2m, 0.72m, 0.6m, 0.12m),
                    TextOverlayStyle.ClosingCta),
            ],
            LogoOverlays =
            [
                new LogoOverlay(
                    "agent-logo", 500, 2_000, new NormalizedRect(0.78m, 0.08m, 0.12m, 0.12m), 0.8m),
                new LogoOverlay(
                    "brokerage-logo", 3_000, 4_000, new NormalizedRect(0.75m, 0.08m, 0.15m, 0.12m), 1m),
            ],
        };
        return request with
        {
            Specification = request.Specification with
            {
                Brand = new BrandKit(
                    "agent-logo", "brokerage-logo", "Avery Agent", "555-0100",
                    "avery@example.test", "example.test", "#17324D", "#F4F0E8"),
                Audio = request.Specification.Audio with
                {
                    Music = new MusicPlan(
                        "music-1", MusicMood.WarmCinematic, 0, 15_000, -18, 1_000, 1_000, -24),
                },
                Scenes = scenes,
            },
            BrandAssets =
            [
                new VideoRenderBrandAsset("agent-logo", agentLogo, 120, 120),
                new VideoRenderBrandAsset("brokerage-logo", brokerageLogo, 180, 90),
            ],
            Music = new VideoRenderMusicAsset("music-1", music),
        };
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1]);
        return path;
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
            [],
            new BrandKit(null, null, null, null, null, null, "#000000", "#ffffff"),
            new GroundedText("Call today", "cta"),
            new AudioPlan(
                [
                    new NarrationSegment("narration-1", 500, 1_000, "Welcome", "voiceover"),
                    new NarrationSegment("narration-2", 9_000, 1_000, "Call today", "cta"),
                ],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    7_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(
                        MotionKind.KenBurns,
                        viewport,
                        new NormalizedRect(0.04m, 0.04m, 0.92m, 0.92m),
                        MotionEasing.EaseInOut),
                    [],
                    [],
                    ["narration-1"]),
                new VideoScene(
                    2,
                    7_000,
                    8_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Crossfade, 500),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [],
                    [],
                    ["narration-2"]),
            ]);
    }
}
