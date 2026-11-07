using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Rendering;

/// <summary>
/// Compares every sampled frame of a Kling video with the presenter's headshot using FFmpeg's SSIM on small, blurred,
/// greyscale copies (so a slow drift or zoom over the photo still matches, while the agent standing in a room does
/// not), then replaces each matching span with a hold of the first frame after it. Audio is copied unchanged.
/// </summary>
public sealed partial class FfmpegPresenterReferenceShotRemover(
    IOptions<FfmpegOptions> options,
    IOptions<PresenterReferenceShotOptions> shotOptions) : IPresenterReferenceShotRemover
{
    // Frames are compared at this size; 9:16 like the presenter video and its cropped headshot.
    private const int CompareWidth = 72;
    private const int CompareHeight = 128;

    public async Task<PresenterReferenceShotRemoval> RemoveAsync(
        string videoPath,
        string referenceImagePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var settings = shotOptions.Value;
        if (!settings.Enabled || !File.Exists(videoPath) || !File.Exists(referenceImagePath))
        {
            return PresenterReferenceShotRemoval.None;
        }

        var duration = await ProbeDurationAsync(videoPath, cancellationToken);
        if (duration is not > 0)
        {
            return PresenterReferenceShotRemoval.None;
        }

        var scores = await ScoreFramesAsync(videoPath, referenceImagePath, settings.SampleFramesPerSecond, cancellationToken);
        var shots = FindShots(scores, settings, duration.Value);
        if (shots.Count == 0)
        {
            return PresenterReferenceShotRemoval.None;
        }

        await WriteWithoutShotsAsync(videoPath, outputPath, shots, duration.Value, cancellationToken);
        return new PresenterReferenceShotRemoval(shots);
    }

    /// <summary>
    /// Turns per-sample similarity scores into spans to cover. Exposed for tests: samples are taken at
    /// <see cref="PresenterReferenceShotOptions.SampleFramesPerSecond"/>, starting at time zero.
    /// </summary>
    public static IReadOnlyList<PresenterReferenceShot> FindShots(
        IReadOnlyList<double> scores,
        PresenterReferenceShotOptions settings,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(settings);
        var step = 1d / settings.SampleFramesPerSecond;
        var spans = new List<(double Start, double End)>();
        for (var index = 0; index < scores.Count; index++)
        {
            var time = index * step;
            // The agent opens on camera; never cover the opening line even if a close-up resembles the headshot.
            if (time < settings.IgnoreFirstSeconds || scores[index] < settings.MatchThreshold)
            {
                continue;
            }

            if (spans.Count > 0 && time - spans[^1].End <= settings.MergeGapSeconds + step)
            {
                spans[^1] = (spans[^1].Start, time + step);
            }
            else
            {
                spans.Add((time, time + step));
            }
        }

        return spans
            .Where(span => span.End - span.Start >= settings.MinimumShotSeconds)
            .Select(span => new PresenterReferenceShot(
                Math.Round(Math.Max(settings.IgnoreFirstSeconds, span.Start - settings.PaddingBeforeSeconds), 3),
                Math.Round(Math.Min(durationSeconds, span.End + settings.PaddingAfterSeconds), 3)))
            .Aggregate(new List<PresenterReferenceShot>(), (merged, shot) =>
            {
                if (merged.Count > 0 && shot.StartSeconds <= merged[^1].EndSeconds)
                {
                    merged[^1] = merged[^1] with { EndSeconds = Math.Max(merged[^1].EndSeconds, shot.EndSeconds) };
                }
                else
                {
                    merged.Add(shot);
                }

                return merged;
            });
    }

    /// <summary>
    /// The FFmpeg filter graph that keeps the picture outside the shots and holds the next kept frame over each shot
    /// (or the previous one when a shot runs to the end), so the length matches the original.
    /// </summary>
    public static string BuildFilterGraph(IReadOnlyList<PresenterReferenceShot> shots, double durationSeconds)
    {
        var keeps = new List<(double Start, double End, double HoldBefore, double HoldAfter)>();
        var cursor = 0d;
        var pendingHold = 0d;
        foreach (var shot in shots)
        {
            if (shot.StartSeconds > cursor)
            {
                keeps.Add((cursor, shot.StartSeconds, pendingHold, 0));
                pendingHold = 0;
            }

            pendingHold += shot.EndSeconds - shot.StartSeconds;
            cursor = shot.EndSeconds;
        }

        if (durationSeconds - cursor > 0.001)
        {
            keeps.Add((cursor, durationSeconds, pendingHold, 0));
        }
        else if (keeps.Count > 0)
        {
            keeps[^1] = keeps[^1] with { HoldAfter = pendingHold };
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"[0:v]split={keeps.Count}");
        for (var index = 0; index < keeps.Count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[s{index}]");
        }

        builder.Append(';');
        for (var index = 0; index < keeps.Count; index++)
        {
            var keep = keeps[index];
            builder.Append(CultureInfo.InvariantCulture,
                $"[s{index}]trim=start={Seconds(keep.Start)}:end={Seconds(keep.End)},setpts=PTS-STARTPTS");
            if (keep.HoldBefore > 0 || keep.HoldAfter > 0)
            {
                builder.Append(CultureInfo.InvariantCulture,
                    $",tpad=start_mode=clone:start_duration={Seconds(keep.HoldBefore)}:stop_mode=clone:stop_duration={Seconds(keep.HoldAfter)}");
            }

            builder.Append(CultureInfo.InvariantCulture, $"[k{index}];");
        }

        for (var index = 0; index < keeps.Count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[k{index}]");
        }

        builder.Append(CultureInfo.InvariantCulture, $"concat=n={keeps.Count}:v=1:a=0,format=yuv420p[v]");
        return builder.ToString();
    }

    private async Task<IReadOnlyList<double>> ScoreFramesAsync(
        string videoPath,
        string referenceImagePath,
        int framesPerSecond,
        CancellationToken cancellationToken)
    {
        var statsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, $"presenter-ssim-{Guid.NewGuid():N}.log");
        try
        {
            var size = $"{CompareWidth}:{CompareHeight}";
            var prepare = $"scale={size}:force_original_aspect_ratio=increase,crop={size},format=gray,boxblur=2:1";
            var result = await RunAsync(
                options.Value.ExecutablePath,
                [
                    "-hide_banner", "-nostdin", "-v", "error",
                    "-i", videoPath,
                    "-loop", "1", "-i", referenceImagePath,
                    "-filter_complex",
                    $"[0:v]fps={framesPerSecond},{prepare}[m];[1:v]fps={framesPerSecond},{prepare}[r];"
                    + $"[m][r]ssim=stats_file={EscapeFilterPath(statsPath)}:shortest=1",
                    "-f", "null", "-",
                ],
                TimeSpan.FromSeconds(Math.Max(60, options.Value.RenderTimeoutSeconds / 4)),
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(statsPath))
            {
                throw new InvalidOperationException($"FFmpeg could not compare the video with the presenter photo ({LastLine(result.Error)}).");
            }

            var scores = new List<double>();
            foreach (var line in await File.ReadAllLinesAsync(statsPath, cancellationToken))
            {
                var match = SsimAllRegex().Match(line);
                if (match.Success
                    && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                {
                    scores.Add(score);
                }
            }

            return scores;
        }
        finally
        {
            File.Delete(statsPath);
        }
    }

    private async Task WriteWithoutShotsAsync(
        string videoPath,
        string outputPath,
        IReadOnlyList<PresenterReferenceShot> shots,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            options.Value.ExecutablePath,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y",
                "-i", videoPath,
                "-filter_complex", BuildFilterGraph(shots, durationSeconds),
                "-map", "[v]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "16",
                "-c:a", "copy",
                "-t", Seconds(durationSeconds),
                "-movflags", "+faststart",
                outputPath,
            ],
            TimeSpan.FromSeconds(options.Value.RenderTimeoutSeconds),
            cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"FFmpeg could not cover the presenter photo shot ({LastLine(result.Error)}).");
        }
    }

    private async Task<double?> ProbeDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        var probe = await RunAsync(
            options.Value.ProbeExecutablePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", videoPath],
            TimeSpan.FromSeconds(60),
            cancellationToken);
        return probe.ExitCode == 0
            && double.TryParse(probe.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? duration
                : null;
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // Filter option values treat ':' and '\' specially; quote the path for the filter parser.
    private static string EscapeFilterPath(string path) =>
        "'" + path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details";

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill request.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (-1, string.Empty, "timed out");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    [GeneratedRegex(@"All:([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SsimAllRegex();
}
