using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    private static readonly Mp4Expectations SmallVertical = new(108, 192, "h264", "aac", 3, 0.35);

    [Fact]
    public async Task TechnicalValidatorPassesAPlayableVerticalMp4AndRejectsBrokenOnes()
    {
        if (Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E") != "1")
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listingstudio-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
            });
            var validator = new FfmpegMp4TechnicalValidator(options);
            var good = Path.Combine(directory, "good.mp4");
            await RunFfmpegAsync(options.Value.ExecutablePath,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=108x192:rate=30:duration=3",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-movflags", "+faststart", good);

            var passed = await validator.ValidateAsync(good, SmallVertical);
            Assert.True(passed.IsValid, passed.Summary);
            Assert.True(passed.Decoded);
            Assert.Equal((108, 192, "h264", "aac"), (passed.Width, passed.Height, passed.VideoCodec, passed.AudioCodec));

            var wrongShape = await validator.ValidateAsync(good, SmallVertical with { Width = 192, Height = 108 });
            Assert.False(wrongShape.IsValid);
            Assert.Contains(wrongShape.Problems, problem => problem.Contains("Frame size", StringComparison.Ordinal));

            var wrongLength = await validator.ValidateAsync(good, SmallVertical with { DurationSeconds = 60 });
            Assert.False(wrongLength.IsValid);
            Assert.Contains(wrongLength.Problems, problem => problem.Contains("Length", StringComparison.Ordinal));

            var silent = Path.Combine(directory, "silent.mp4");
            await RunFfmpegAsync(options.Value.ExecutablePath,
                "-hide_banner", "-loglevel", "error", "-nostdin", "-i", good, "-an", "-c:v", "copy", silent);
            var noAudio = await validator.ValidateAsync(silent, SmallVertical);
            Assert.False(noAudio.IsValid);
            Assert.Contains(noAudio.Problems, problem => problem.Contains("audio stream", StringComparison.Ordinal));

            // A render cut off part way: the header still parses, but the frames do not decode to the end.
            var bytes = await File.ReadAllBytesAsync(good);
            var truncated = Path.Combine(directory, "truncated.mp4");
            await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length * 2 / 3)]);
            var cutOff = await validator.ValidateAsync(truncated, SmallVertical);
            Assert.False(cutOff.IsValid);

            var empty = Path.Combine(directory, "empty.mp4");
            await File.WriteAllBytesAsync(empty, []);
            Assert.False((await validator.ValidateAsync(empty, SmallVertical)).IsValid);
            Assert.False((await validator.ValidateAsync(Path.Combine(directory, "missing.mp4"), SmallVertical)).IsValid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
