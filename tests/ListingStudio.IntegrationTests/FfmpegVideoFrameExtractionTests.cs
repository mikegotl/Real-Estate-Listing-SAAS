using System.Diagnostics;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class FfmpegVideoFrameExtractionTests
{
    [Fact]
    public async Task ExtractsOneSharpStillFromEachShotOfARealVideo()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-frame-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Three 3-second shots with hard cuts between them, like a walkthrough edited room by room.
            var videoPath = Path.Combine(directory, "tour.mp4");
            await RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3",
                "-f", "lavfi", "-i", "smptehdbars=s=640x360:r=30:d=3",
                "-f", "lavfi", "-i", "mandelbrot=s=640x360:r=30,trim=duration=3",
                "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p",
                "-c:v", "libx264", "-preset", "ultrafast", videoPath,
            ]);
            var extractor = new FfmpegPropertyVideoFrameExtractor(Options.Create(new FfmpegOptions
            {
                ExecutablePath = ffmpeg,
                EnhancementTimeoutSeconds = 120,
            }));

            await using var video = File.OpenRead(videoPath);
            var stills = await extractor.ExtractCandidatesAsync(video, "tour.mp4", maximumCandidates: 24);

            Assert.Equal(3, stills.Count);
            Assert.Collection(
                stills,
                first => Assert.InRange(first.TimestampMs, 0, 2_999),
                second => Assert.InRange(second.TimestampMs, 3_000, 5_999),
                third => Assert.InRange(third.TimestampMs, 6_000, 9_000));
            Assert.All(stills, still =>
            {
                Assert.Equal([0xFF, 0xD8], still.JpegContent[..2]);
                Assert.True(still.Sharpness > 0);
            });
            Assert.Single(await ExtractAsync(extractor, videoPath, maximumCandidates: 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FindsSteadyClipsAndRoomPhotosInAnHdrPhoneVideo()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-clip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Ten seconds of a slowly moving, detailed 10-bit picture tagged the way an iPhone tags HLG video.
            var videoPath = Path.Combine(directory, "hdr.mov");
            await RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=10",
                "-vf", "format=yuv420p10le",
                "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error",
                "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc",
                videoPath,
            ]);
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = ffmpeg,
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
                EnhancementTimeoutSeconds = 120,
            });

            await using (var video = File.OpenRead(videoPath))
            {
                var spans = await new FfmpegPropertyVideoClipFinder(options).FindClipsAsync(video, "hdr.mov");
                Assert.NotEmpty(spans);
                Assert.All(spans, span =>
                {
                    Assert.InRange(span.EndMs - span.StartMs, 2_000, 4_000);
                    Assert.InRange(span.StartMs, 500, 10_000);
                    Assert.True(span.Score > 0);
                });
            }

            var stills = await ExtractAsync(new FfmpegPropertyVideoFrameExtractor(options), videoPath, maximumCandidates: 4);
            Assert.NotEmpty(stills);
            Assert.All(stills, still => Assert.Equal([0xFF, 0xD8], still.JpegContent[..2]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<ListingStudio.Application.Properties.VideoFrameCandidate>> ExtractAsync(
        FfmpegPropertyVideoFrameExtractor extractor,
        string path,
        int maximumCandidates)
    {
        await using var video = File.OpenRead(path);
        return await extractor.ExtractCandidatesAsync(video, "tour.mp4", maximumCandidates);
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
