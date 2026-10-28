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
        Assert.Contains("perspective=", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("zoompan", graph, StringComparison.Ordinal);
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
    public void BuildBoundsFfmpegParallelismForContainerMemorySafety()
    {
        var command = FfmpegCommandBuilder.Build(CreateRequest());
        var arguments = command.Arguments.ToList();

        Assert.Equal("1", arguments[arguments.IndexOf("-filter_complex_threads") + 1]);
        Assert.Equal("faster", arguments[arguments.IndexOf("-preset") + 1]);
        Assert.Equal("1", arguments[arguments.IndexOf("-threads") + 1]);
    }

    [Fact]
    public void BuildUsesBothWorkerCoresForLipSyncedPresenterRenders()
    {
        var request = CreateBrandedRequest();
        var portraitId = "agent-portrait:11111111111111111111111111111111";
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            LogoOverlays = [new LogoOverlay(
                portraitId,
                0,
                4_000,
                new NormalizedRect(0.72m, 0.5m, 0.22m, 0.38m),
                0.98m)],
        };
        scenes[^1] = scenes[^1] with
        {
            LogoOverlays = [new LogoOverlay(
                portraitId,
                3_000,
                3_000,
                new NormalizedRect(0.78m, 0.6m, 0.16m, 0.28m),
                0.95m)],
        };
        request = request with
        {
            Specification = request.Specification with
            {
                Brand = request.Specification.Brand with { SecondaryLogo = portraitId },
                Scenes = scenes,
            },
            BrandAssets = [new VideoRenderBrandAsset(portraitId, CreateFile("agent.jpg"), 800, 1_000)],
            AgentPresenter = new VideoRenderAgentPresenterAsset(
                portraitId,
                CreateFile("lip-synced-presenter.mp4"),
                720,
                720,
                500,
                4_000,
                9_000,
                3_000),
        };

        var arguments = FfmpegCommandBuilder.Build(request).Arguments.ToList();

        Assert.Equal("2", arguments[arguments.IndexOf("-filter_complex_threads") + 1]);
        Assert.Equal("veryfast", arguments[arguments.IndexOf("-preset") + 1]);
        Assert.Equal("2", arguments[arguments.IndexOf("-threads") + 1]);
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
    public void BuildPadsAPhotoToTheOutputShapeBeforeKenBurnsSoItIsNotStretched()
    {
        var request = CreateRequest();
        var fill = new NormalizedRect(0, 0.125m, 1, 0.75m);
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            Motion = scenes[0].Motion with { StartViewport = fill, EndViewport = new NormalizedRect(0.1m, 0.2m, 0.8m, 0.6m) },
        };
        scenes[1] = scenes[1] with { Motion = scenes[1].Motion with { StartViewport = fill, EndViewport = fill } };
        request = request with
        {
            Specification = request.Specification with { Scenes = scenes },
            PropertyMedia = [request.PropertyMedia[0] with { Width = 320, Height = 240 }],
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        // 4:3 pads to 427x240; the window travels over 320x180 of the photo, from its full width to 256 pixels wide.
        Assert.Contains("[0:v]pad=427:240:53:0:color=black,setsar=1,crop=320:180:53:30,perspective=x0='0+(32)*(", graph, StringComparison.Ordinal);
        Assert.Contains(":x3='320+(-32)*(", graph, StringComparison.Ordinal);
        Assert.Contains(":y3='180+(-18)*(", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAcceptsTimingForSegmentsThatAShorterCutLeftOut()
    {
        var request = CreateRequest() with
        {
            Narration = new VideoRenderNarrationAsset(
                CreateFile("campaign-narration.wav"),
                new VoiceTimingMetadata(
                    [],
                    [
                        new VoiceSegmentTiming("narration-1", 0, 500),
                        new VoiceSegmentTiming("narration-left-out", 500, 4_000),
                        new VoiceSegmentTiming("narration-2", 4_000, 4_500),
                    ])),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("atrim=start=4:end=4.5,asetpts=PTS-STARTPTS,adelay=9000:all=1", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("end=4,", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDucksMusicWhereAnOverrunningVoiceIsActuallyHeard()
    {
        var request = CreateBrandedRequest() with
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
        Assert.Contains("volume='if(gt(between(t,0.5,9.5)+between(t,9.5,10.5),0)", graph, StringComparison.Ordinal);
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
        Assert.Contains("scale=1080:1920:flags=lanczos,setsar=1", graph, StringComparison.Ordinal);
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
        Assert.Contains("volume='if(gt(between(t,0.5,1)+between(t,9,9.5),0)", graph, StringComparison.Ordinal);
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
    public void BuildUsesLipSyncedPresenterAtNarrationTimesAndKeepsOriginalNarrationAudio()
    {
        var request = CreateBrandedRequest();
        var portraitId = "agent-portrait:11111111111111111111111111111111";
        var presenterPath = CreateFile("lip-synced-presenter.mp4");
        var scenes = request.Specification.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            LogoOverlays = [new LogoOverlay(
                portraitId,
                0,
                4_000,
                new NormalizedRect(0.72m, 0.5m, 0.22m, 0.38m),
                0.98m)],
        };
        scenes[^1] = scenes[^1] with
        {
            LogoOverlays = [new LogoOverlay(
                portraitId,
                3_000,
                3_000,
                new NormalizedRect(0.78m, 0.6m, 0.16m, 0.28m),
                0.95m)],
        };
        request = request with
        {
            Specification = request.Specification with
            {
                Brand = request.Specification.Brand with { SecondaryLogo = portraitId },
                Scenes = scenes,
            },
            BrandAssets = [new VideoRenderBrandAsset(portraitId, CreateFile("agent.jpg"), 800, 1_000)],
            AgentPresenter = new VideoRenderAgentPresenterAsset(
                portraitId,
                presenterPath,
                720,
                720,
                500,
                4_000,
                9_000,
                3_000),
        };

        var command = FfmpegCommandBuilder.Build(request);

        Assert.Contains(presenterPath, command.Arguments);
        Assert.Contains(request.Narration!.FilePath, command.Arguments);
        var graph = command.Arguments[command.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("split=2[agentPresenterSource0][agentPresenterSource1]", graph, StringComparison.Ordinal);
        Assert.Contains("trim=start=0:end=4,setpts=PTS-STARTPTS+0.5/TB", graph, StringComparison.Ordinal);
        Assert.Contains("trim=start=4:end=7,setpts=PTS-STARTPTS+9/TB", graph, StringComparison.Ordinal);
        Assert.Contains("between(t,0.5,4.5)", graph, StringComparison.Ordinal);
        Assert.Contains("between(t,9,12)", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("colorchannelmixer=aa=0.98", graph, StringComparison.Ordinal);
        Assert.Contains("[narration0]", graph, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{command.Arguments.ToList().IndexOf(presenterPath)}:a]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsPresenterOutsideProgramTimeline()
    {
        var request = CreateBrandedRequest();
        var portraitId = request.Specification.Brand.Logo!;
        request = request with
        {
            AgentPresenter = new VideoRenderAgentPresenterAsset(
                portraitId,
                CreateFile("invalid-presenter.mp4"),
                720,
                720,
                14_000,
                2_000,
                0,
                0),
        };

        var exception = Assert.Throws<ArgumentException>(() => FfmpegCommandBuilder.Build(request));

        Assert.Contains("presenter", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public void BuildPlaysAWalkthroughShotFromAfterItsLeadInSpedUpToFitTheShot()
    {
        var request = CreateRequest();
        var clipId = Guid.NewGuid();
        var clipPath = CreateFile("walkthrough shot.mp4");
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
            GeneratedClips =
            [
                new VideoRenderGeneratedClipAsset(clipId, clipPath, 1_920, 1_080, 60_000, LeadInMs: 1_000, FitToScene: true),
            ],
        };
        var requiredMs = scenes[0].DurationMs + scenes[1].TransitionIn.DurationMs;

        var arguments = FfmpegCommandBuilder.Build(request).Arguments.ToList();

        // A minute-long clip in a shorter shot plays at the two-times limit from just after its lead-in.
        var input = arguments.IndexOf(clipPath);
        Assert.Equal(["-ss", "1", "-t"], arguments.Skip(input - 5).Take(3));
        Assert.Equal((requiredMs * 2 / 1_000m).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), arguments[input - 2]);
        Assert.DoesNotContain("-stream_loop", arguments);
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("crop=1920:1080,setsar=1,setpts=(PTS-STARTPTS)/2,fps=", graph, StringComparison.Ordinal);
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

    [Fact]
    public void BuildPlaysWalkthroughClipAtTheEndOfTheFirstPhotoAndCutsToTheNext()
    {
        var (request, clip) = CreateWalkthroughRequest(clipDurationMs: 5_000);

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        Assert.Contains(clip, arguments);
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("[scene0still]trim=duration=2,setpts=PTS-STARTPTS[scene0hold]", graph, StringComparison.Ordinal);
        Assert.Contains("setpts=1*(PTS-STARTPTS)", graph, StringComparison.Ordinal);
        Assert.Contains("tpad=stop_mode=clone:stop_duration=5,trim=duration=5", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0hold][scene0walk]concat=n=2:v=1:a=0[scene0]", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0][scene1]concat=n=2:v=1:a=0[timeline1]", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("xfade", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("zoompan", graph, StringComparison.Ordinal);
        Assert.Contains("adelay=9000:all=1", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSpeedsUpALongWalkthroughToFitItsScene()
    {
        var (request, _) = CreateWalkthroughRequest(clipDurationMs: 10_000);

        var command = FfmpegCommandBuilder.Build(request);

        var graph = command.Arguments[command.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("[scene0still]trim=duration=1,", graph, StringComparison.Ordinal);
        Assert.Contains("setpts=0.6*(PTS-STARTPTS)", graph, StringComparison.Ordinal);
        Assert.Contains("trim=duration=6,setpts=PTS-STARTPTS[scene0walk]", graph, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20_000, false)]
    [InlineData(5_000, true)]
    public void BuildKeepsThePlannedTransitionWhenAWalkthroughCannotFitOrDoesNotMatch(int clipDurationMs, bool reversed)
    {
        var (request, clip) = CreateWalkthroughRequest(clipDurationMs, reversed);

        var command = FfmpegCommandBuilder.Build(request);

        Assert.DoesNotContain(clip, command.Arguments);
        var graph = command.Arguments[command.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("xfade=transition=fade:duration=0.5:offset=7", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("scene0walk", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOpensWithTheDroneIntroThenHoldsTheFrontPhoto()
    {
        var request = CreateRequest();
        var intro = CreateFile("aerial-5000.mp4");
        request = request with
        {
            AerialIntro = new VideoRenderAerialIntroAsset(request.PropertyMedia[0].PropertyMediaId, intro, 5_000),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var input = arguments.IndexOf(intro);
        Assert.Equal(["-ss", "0", "-t", "5", "-i"], arguments.GetRange(input - 5, 5));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("setpts=1*(PTS-STARTPTS)", graph, StringComparison.Ordinal);
        Assert.Contains("trim=duration=5,setpts=PTS-STARTPTS[scene0intro]", graph, StringComparison.Ordinal);
        // The photo holds for the rest of the scene, including the crossfade into the next one.
        Assert.Contains("[scene0still]trim=duration=2.5,setpts=PTS-STARTPTS[scene0body]", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0intro][scene0body]concat=n=2:v=1:a=0[scene0]", graph, StringComparison.Ordinal);
        Assert.Contains("xfade=transition=fade:duration=0.5:offset=7", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlaysOnlyTheEndOfALongDroneIntroAtUpToDoubleSpeed()
    {
        var request = CreateRequest();
        var intro = CreateFile("aerial-14000.mp4");
        request = request with
        {
            AerialIntro = new VideoRenderAerialIntroAsset(request.PropertyMedia[0].PropertyMediaId, intro, 14_000),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var input = arguments.IndexOf(intro);
        // Six seconds of scene time play the last twelve seconds of the descent, which land on the photo.
        Assert.Equal(["-ss", "2", "-t", "12", "-i"], arguments.GetRange(input - 5, 5));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("setpts=0.5*(PTS-STARTPTS)", graph, StringComparison.Ordinal);
        Assert.Contains("trim=duration=6,setpts=PTS-STARTPTS[scene0intro]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFitsTheDroneIntroBeforeAWalkthroughOutOfTheFrontPhoto()
    {
        var (request, _) = CreateWalkthroughRequest(clipDurationMs: 5_000);
        var intro = CreateFile("aerial-with-walk.mp4");
        request = request with
        {
            AerialIntro = new VideoRenderAerialIntroAsset(request.PropertyMedia[0].PropertyMediaId, intro, 5_000),
        };

        var command = FfmpegCommandBuilder.Build(request);

        var arguments = command.Arguments.ToList();
        var input = arguments.IndexOf(intro);
        Assert.Equal(["-ss", "3", "-t", "2", "-i"], arguments.GetRange(input - 5, 5));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("trim=duration=1,setpts=PTS-STARTPTS[scene0intro]", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0still]trim=duration=1,setpts=PTS-STARTPTS[scene0hold]", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0hold][scene0walk]concat=n=2:v=1:a=0[scene0body]", graph, StringComparison.Ordinal);
        Assert.Contains("[scene0intro][scene0body]concat=n=2:v=1:a=0[scene0]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLeavesOutADroneIntroForAnotherPhoto()
    {
        var request = CreateRequest();
        var intro = CreateFile("aerial-other.mp4");
        request = request with { AerialIntro = new VideoRenderAerialIntroAsset(Guid.NewGuid(), intro, 5_000) };

        var command = FfmpegCommandBuilder.Build(request);

        Assert.DoesNotContain(intro, command.Arguments);
        Assert.DoesNotContain("scene0intro", command.Arguments[command.Arguments.ToList().IndexOf("-filter_complex") + 1], StringComparison.Ordinal);
    }

    private (VideoRenderRequest Request, string ClipPath) CreateWalkthroughRequest(
        int clipDurationMs,
        bool reversed = false)
    {
        var request = CreateRequest();
        var firstId = request.PropertyMedia[0].PropertyMediaId;
        var secondId = Guid.NewGuid();
        var scenes = request.Specification.Scenes.ToArray();
        scenes[1] = scenes[1] with
        {
            VisualSource = new VisualSource(VisualSourceKind.PropertyMedia, secondId, null, null, null),
        };
        var clip = CreateFile($"walkthrough-{clipDurationMs}.mp4");
        return (request with
        {
            Specification = request.Specification with { Scenes = scenes },
            PropertyMedia =
            [
                .. request.PropertyMedia,
                new VideoRenderMediaAsset(secondId, CreateFile("next room.ppm"), 320, 180),
            ],
            WalkthroughTransitions =
            [
                reversed
                    ? new VideoRenderWalkthroughTransitionAsset(secondId, firstId, clip, clipDurationMs)
                    : new VideoRenderWalkthroughTransitionAsset(firstId, secondId, clip, clipDurationMs),
            ],
        }, clip);
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
