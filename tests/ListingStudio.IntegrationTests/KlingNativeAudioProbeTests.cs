using System.Diagnostics;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>
/// The native-presenter render uses Kling's own sound as the whole soundtrack, so a Kling video without an audio
/// track must be recognised before the render instead of failing inside FFmpeg.
/// </summary>
public sealed class KlingNativeAudioProbeTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("kling-audio-probe-").FullName;

    private static readonly FfmpegOptions Ffmpeg = new()
    {
        ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
        ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
    };

    [Fact]
    public async Task ProbeTellsASilentKlingVideoFromOneWithSound()
    {
        var silent = Path.Combine(directory, "silent.mp4");
        var withSound = Path.Combine(directory, "sound.mp4");
        await RunFfmpegAsync("-f", "lavfi", "-i", "color=c=navy:s=320x568:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", silent);
        await RunFfmpegAsync(
            "-f", "lavfi", "-i", "color=c=navy:s=320x568:d=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", withSound);
        var validator = new FfmpegMp4TechnicalValidator(Options.Create(Ffmpeg));

        Assert.False(await validator.HasAudioStreamAsync(silent));
        Assert.True(await validator.HasAudioStreamAsync(withSound));
        Assert.Null(await validator.HasAudioStreamAsync(Path.Combine(directory, "missing.mp4")));
    }

    private static readonly string[] QuietOverwrite = ["-v", "error", "-y"];

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Ffmpeg.ExecutablePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in QuietOverwrite.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
