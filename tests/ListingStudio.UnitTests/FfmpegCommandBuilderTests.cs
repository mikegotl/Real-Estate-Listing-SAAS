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
    public void BuildRejectsMultipleNarrationSegmentsWithoutTiming()
    {
        var request = CreateRequest() with
        {
            Narration = new VideoRenderNarrationAsset(CreateFile("narration-untimed.wav"), null),
        };

        var exception = Assert.Throws<ArgumentException>(() => FfmpegCommandBuilder.Build(request));

        Assert.Contains("require measured timing", exception.Message, StringComparison.Ordinal);
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
        }));

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
            output);
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
            new VideoBrandPlan(null, null, null, null, null, null, "#000000", "#ffffff"),
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
