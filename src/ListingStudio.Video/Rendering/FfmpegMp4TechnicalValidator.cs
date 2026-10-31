using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Rendering;

/// <summary>
/// Checks a rendered MP4 with ffprobe (container, codecs, frame size, length) and then decodes every frame of it with
/// FFmpeg, stopping at the first decoding error. A browser that starts playing a file proves much less than this.
/// </summary>
public sealed class FfmpegMp4TechnicalValidator(IOptions<FfmpegOptions> options) : IRenderedVideoValidator
{
    public async Task<RenderedVideoValidation> ValidateAsync(
        string path,
        Mp4Expectations expectations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectations);
        var configuration = options.Value;
        var problems = new List<string>();
        if (!File.Exists(path))
        {
            return Failed("The file does not exist.");
        }

        if (new FileInfo(path).Length == 0)
        {
            return Failed("The file is empty.");
        }

        // A decode runs much faster than real time; allow generously for long-form videos on a busy Worker.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(60 + expectations.DurationSeconds * 2, 60, 1_800));
        var probe = await RunAsync(
            configuration.ProbeExecutablePath,
            [
                "-v", "error",
                "-show_entries", "stream=codec_type,codec_name,width,height",
                "-show_entries", "format=format_name,duration",
                "-of", "json",
                path,
            ],
            timeout,
            cancellationToken);
        if (probe.ExitCode != 0)
        {
            return Failed($"ffprobe could not read the file ({LastLine(probe.Error)}).");
        }

        int? width = null;
        int? height = null;
        string? videoCodec = null;
        string? audioCodec = null;
        double? duration = null;
        try
        {
            using var document = JsonDocument.Parse(probe.Output);
            var root = document.RootElement;
            if (root.TryGetProperty("streams", out var streams))
            {
                var videoStreams = streams.EnumerateArray()
                    .Where(stream => Text(stream, "codec_type") == "video").ToArray();
                var audioStreams = streams.EnumerateArray()
                    .Where(stream => Text(stream, "codec_type") == "audio").ToArray();
                if (videoStreams.Length != 1)
                {
                    problems.Add($"Expected one video stream but found {videoStreams.Length}.");
                }

                if (audioStreams.Length != 1)
                {
                    problems.Add($"Expected one audio stream but found {audioStreams.Length}.");
                }

                if (videoStreams.Length > 0)
                {
                    videoCodec = Text(videoStreams[0], "codec_name");
                    width = videoStreams[0].TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    height = videoStreams[0].TryGetProperty("height", out var h) ? h.GetInt32() : null;
                }

                if (audioStreams.Length > 0)
                {
                    audioCodec = Text(audioStreams[0], "codec_name");
                }
            }

            if (root.TryGetProperty("format", out var format))
            {
                if (Text(format, "format_name") is { } formatName && !formatName.Contains("mp4", StringComparison.Ordinal))
                {
                    problems.Add($"The container is {formatName}, not MP4.");
                }

                if (Text(format, "duration") is { } text
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    duration = seconds;
                }
            }
        }
        catch (JsonException)
        {
            return Failed("ffprobe returned unreadable output.");
        }

        if (!string.Equals(videoCodec, expectations.VideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Video codec is {videoCodec ?? "missing"}, expected {expectations.VideoCodec}.");
        }

        if (!string.Equals(audioCodec, expectations.AudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Audio codec is {audioCodec ?? "missing"}, expected {expectations.AudioCodec}.");
        }

        if (width != expectations.Width || height != expectations.Height)
        {
            problems.Add($"Frame size is {width}x{height}, expected {expectations.Width}x{expectations.Height}.");
        }

        if (duration is null)
        {
            problems.Add("The length could not be read.");
        }
        else if (Math.Abs(duration.Value - expectations.DurationSeconds) > expectations.DurationToleranceSeconds)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Length is {duration.Value:0.00}s, expected {expectations.DurationSeconds:0.00}s."));
        }

        var decode = await RunAsync(
            configuration.ExecutablePath,
            ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-i", path, "-map", "0", "-f", "null", "-"],
            timeout,
            cancellationToken);
        var decoded = decode.ExitCode == 0 && string.IsNullOrWhiteSpace(decode.Error);
        if (!decoded)
        {
            problems.Add($"The file did not decode cleanly from start to finish ({LastLine(decode.Error)}).");
        }

        return new RenderedVideoValidation(
            problems.Count == 0, problems, width, height, videoCodec, audioCodec, duration, decoded);

        RenderedVideoValidation Failed(string problem) =>
            new(false, [problem], null, null, null, null, null, false);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The last diagnostic line, without the file path FFmpeg sometimes prints.</summary>
    private static string LastLine(string error)
    {
        var line = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? (error.Length == 0 ? "no diagnostic output" : error);
        var colon = line.LastIndexOf(": ", StringComparison.Ordinal);
        if (line.Contains('/') && colon > 0)
        {
            line = line[(colon + 2)..];
        }

        return line.Length <= 300 ? line : line[..300];
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
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
}
