using System.Diagnostics;
using System.Globalization;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class FfmpegPresenterReferenceShotRemoverTests
{
    // Per-frame SSIM (10 per second) measured on the 2026-11-07 Gulley Kling 4 clip against the 9:16 headshot sent as
    // image_urls[0]: the agent opens in the living room, Kling cuts to the headshot photo at 5.0 s and whip-pans out of
    // it into the exterior by 5.7 s.
    private const string GulleyScores =
        "0.176 0.181 0.168 0.148 0.154 0.157 0.164 0.172 0.168 0.179 0.178 0.173 0.177 0.195 0.228 0.205 0.225 0.278 "
        + "0.312 0.298 0.281 0.267 0.252 0.213 0.207 0.205 0.200 0.192 0.125 0.127 0.137 0.144 0.135 0.148 0.159 0.169 "
        + "0.160 0.158 0.157 0.158 0.159 0.159 0.150 0.149 0.156 0.153 0.149 0.152 0.152 0.145 0.997 0.996 0.997 0.997 "
        + "0.996 0.950 0.714 0.284 0.176 0.202 0.274 0.275 0.294 0.300 0.295 0.276 0.279 0.273 0.253 0.238 0.232 0.218 "
        + "0.247 0.196 0.239 0.235 0.267 0.211 0.150 0.146 0.189 0.221 0.227 0.196 0.223 0.256 0.274 0.293 0.284 0.284 "
        + "0.261 0.269 0.247 0.274 0.269 0.246 0.249 0.217 0.209 0.162 0.213 0.242 0.235 0.269 0.286 0.271 0.279 0.261 "
        + "0.230 0.206 0.226 0.248 0.244 0.219 0.221 0.147 0.156 0.135 0.119 0.107 0.131 0.121 0.151 0.176 0.188 0.220 "
        + "0.255 0.269 0.264 0.253 0.169 0.150 0.194 0.193 0.195 0.194 0.212 0.201 0.234 0.231 0.227 0.240 0.280 0.348 "
        + "0.338 0.338 0.328 0.316 0.302 0.289";

    [Fact]
    public void FindsOnlyTheHeadshotShotInTheRealGulleyClipAndCoversItsPanOut()
    {
        var scores = GulleyScores.Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

        var shot = Assert.Single(FfmpegPresenterReferenceShotRemover.FindShots(scores, new PresenterReferenceShotOptions(), 15.04));

        Assert.Equal(4.9, shot.StartSeconds, 3);
        Assert.Equal(6.1, shot.EndSeconds, 3);
    }

    [Fact]
    public void NeverCoversTheOpeningLineAndIgnoresAPassingResemblance()
    {
        var settings = new PresenterReferenceShotOptions();
        var scores = new double[40];
        scores[3] = 0.9;   // 0.3 s: the agent's opening close-up
        scores[20] = 0.9;  // 2.0 s: one stray frame, shorter than a shot

        Assert.Empty(FfmpegPresenterReferenceShotRemover.FindShots(scores, settings, 4));
    }

    [Fact]
    public void HoldsTheNextShotOverACoveredShotOrThePreviousOneAtTheEnd()
    {
        var middle = FfmpegPresenterReferenceShotRemover.BuildFilterGraph(
            [new(4.9, 6.1)], 15);
        Assert.Equal(
            "[0:v]split=2[s0][s1];[s0]trim=start=0:end=4.9,setpts=PTS-STARTPTS[k0];"
            + "[s1]trim=start=6.1:end=15,setpts=PTS-STARTPTS,tpad=start_mode=clone:start_duration=1.2:stop_mode=clone:stop_duration=0[k1];"
            + "[k0][k1]concat=n=2:v=1:a=0,format=yuv420p[v]",
            middle);

        var atEnd = FfmpegPresenterReferenceShotRemover.BuildFilterGraph([new(13.5, 15)], 15);
        Assert.Contains("tpad=start_mode=clone:start_duration=0:stop_mode=clone:stop_duration=1.5", atEnd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoversAHeadshotShotInARealVideoWithoutChangingItsLengthOrSound()
    {
        if (!CanRun("ffmpeg") || !CanRun("ffprobe"))
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("presenter-shot-").FullName;
        try
        {
            var headshot = Path.Combine(directory, "headshot.png");
            var video = Path.Combine(directory, "kling.mp4");
            var output = Path.Combine(directory, "cleaned.mp4");
            // A "headshot" (diagonal stripes) shown between two "rooms" (solid colours), with a tone for sound.
            await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i",
                "nullsrc=s=360x640,geq=lum='if(lt(mod(X+Y,80),40),230,30)':cb=128:cr=128",
                "-frames:v", "1", headshot);
            await RunAsync("ffmpeg", "-v", "error", "-y",
                "-f", "lavfi", "-i", "color=c=0x8a6d4b:s=360x640:r=24:d=2",
                "-loop", "1", "-framerate", "24", "-t", "1", "-i", headshot,
                "-f", "lavfi", "-i", "color=c=0x4b7a8a:s=360x640:r=24:d=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
                "-filter_complex", "[0:v]format=yuv420p[a];[1:v]format=yuv420p,setsar=1[b];[2:v]format=yuv420p[c];[a][b][c]concat=n=3:v=1:a=0[v]",
                "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-c:a", "aac", "-shortest", video);

            var remover = new FfmpegPresenterReferenceShotRemover(
                Options.Create(new FfmpegOptions()),
                Options.Create(new PresenterReferenceShotOptions()));
            var removal = await remover.RemoveAsync(video, headshot, output);

            var shot = Assert.Single(removal.Shots);
            Assert.InRange(shot.StartSeconds, 1.8, 2.0);
            Assert.InRange(shot.EndSeconds, 3.0, 3.6);
            Assert.InRange(await DurationAsync(output), await DurationAsync(video) - 0.1, await DurationAsync(video) + 0.1);
            Assert.Contains("audio", await RunAsync("ffprobe", "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", output), StringComparison.Ordinal);

            // Checking the cleaned video again finds nothing left to cover.
            Assert.False((await remover.RemoveAsync(output, headshot, Path.Combine(directory, "again.mp4"))).Removed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<double> DurationAsync(string path) => double.Parse(
        (await RunAsync("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path)).Trim(),
        CultureInfo.InvariantCulture);

    private static bool CanRun(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{executable} failed: {error}");
        return output;
    }
}
