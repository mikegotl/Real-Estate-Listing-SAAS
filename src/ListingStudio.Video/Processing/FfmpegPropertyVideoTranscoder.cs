using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Processing;

public sealed class FfmpegPropertyVideoTranscoder(IOptions<FfmpegOptions> options) : IPropertyVideoTranscoder
{
    public const string CurrentEnhancementVersion = "ffmpeg-stabilize-color-v1";

    public string EnhancementVersion => CurrentEnhancementVersion;

    public async Task<PropertyVideoMetadata> ProbeAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        var directory = CreateTemporaryDirectory();
        try
        {
            var inputPath = Path.Combine(directory, $"input{SafeExtension(originalFilename)}");
            await CopyToFileAsync(content, inputPath, cancellationToken);
            return await ProbeFileAsync(inputPath, cancellationToken);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public async Task<PropertyVideoEnhancementResult> EnhanceAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        var directory = CreateTemporaryDirectory();
        try
        {
            var inputPath = Path.Combine(directory, $"input{SafeExtension(originalFilename)}");
            var outputPath = Path.Combine(directory, "enhanced.mp4");
            await CopyToFileAsync(content, inputPath, cancellationToken);

            await RunAsync(
                options.Value.ExecutablePath,
                directory,
                [
                    "-hide_banner", "-loglevel", "warning", "-nostdin", "-y",
                    "-i", inputPath,
                    "-map", "0:v:0",
                    "-vf", "vidstabdetect=shakiness=6:accuracy=15:stepsize=6:mincontrast=0.3:result=transforms.trf",
                    "-f", "null", NullOutput,
                ],
                options.Value.EnhancementTimeoutSeconds,
                cancellationToken);

            await RunAsync(
                options.Value.ExecutablePath,
                directory,
                BuildEnhancementArguments(inputPath, outputPath),
                options.Value.EnhancementTimeoutSeconds,
                cancellationToken);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            {
                throw new InvalidDataException("FFmpeg did not produce an enhanced video.");
            }

            var metadata = await ProbeFileAsync(outputPath, cancellationToken);
            var fileSize = new FileInfo(outputPath).Length;
            var stream = new TemporaryOutputStream(outputPath, directory);
            return new PropertyVideoEnhancementResult(stream, fileSize, metadata);
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }
    }

    public static IReadOnlyList<string> BuildEnhancementArguments(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        const string filters =
            "vidstabtransform=input=transforms.trf:smoothing=30:optzoom=1:zoom=0:interpol=bicubic,"
            + "deflicker=size=9:mode=pm,"
            + "grayworld,"
            + "normalize=smoothing=50:independence=0.85,"
            + "eq=contrast=1.02:saturation=1.03,"
            + "fps=30,scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1";

        return
        [
            "-hide_banner", "-loglevel", "warning", "-nostdin", "-y",
            "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", filters,
            "-c:v", "libx264", "-preset", "medium", "-crf", "20",
            "-pix_fmt", "yuv420p", "-fps_mode", "cfr",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", "-max_muxing_queue_size", "1024",
            outputPath,
        ];
    }

    private async Task<PropertyVideoMetadata> ProbeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            options.Value.ProbeExecutablePath,
            Path.GetDirectoryName(path)!,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            30,
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(output.StandardOutput);
            var root = document.RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(stream =>
                stream.TryGetProperty("codec_type", out var type)
                && string.Equals(type.GetString(), "video", StringComparison.Ordinal));
            if (video.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidDataException("The upload does not contain a video stream.");
            }

            var format = root.GetProperty("format");
            var container = format.TryGetProperty("format_name", out var formatName)
                ? formatName.GetString() ?? string.Empty
                : string.Empty;
            var durationText = format.TryGetProperty("duration", out var duration)
                ? duration.GetString()
                : video.TryGetProperty("duration", out var streamDuration) ? streamDuration.GetString() : null;
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds))
            {
                throw new InvalidDataException("The video duration could not be determined.");
            }

            var frameRateText = video.TryGetProperty("avg_frame_rate", out var averageRate)
                ? averageRate.GetString()
                : null;
            var frameRate = ParseFrameRate(frameRateText);
            var width = video.GetProperty("width").GetInt32();
            var height = video.GetProperty("height").GetInt32();
            var hasAudio = streams.Any(stream =>
                stream.TryGetProperty("codec_type", out var type)
                && string.Equals(type.GetString(), "audio", StringComparison.Ordinal));
            return new PropertyVideoMetadata(
                container,
                width,
                height,
                checked((int)Math.Round(durationSeconds * 1_000, MidpointRounding.AwayFromZero)),
                frameRate,
                hasAudio);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("FFprobe returned invalid video metadata.", exception);
        }
    }

    private static decimal ParseFrameRate(string? value)
    {
        var parts = value?.Split('/', 2, StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 2
            && decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
            ? Math.Round(direct, 3)
            : 0;
    }

    private static async Task CopyToFileAsync(Stream content, string path, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await using var target = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.CopyToAsync(target, cancellationToken);
    }

    private static async Task<ProcessOutput> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
            }
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.", exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded the {timeoutSeconds}-second timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {UsefulError(error)}");
        }

        return new ProcessOutput(output, error);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string UsefulError(string error)
    {
        var normalized = error.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 1_000 ? normalized : normalized[^1_000..];
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"listing-studio-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SafeExtension(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mov" or ".webm" ? extension : ".video";
    }

    private static string NullOutput => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record ProcessOutput(string StandardOutput, string StandardError);

    private sealed class TemporaryOutputStream(string path, string directory) : Stream
    {
        private readonly FileStream inner = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                DeleteDirectory(directory);
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            DeleteDirectory(directory);
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
