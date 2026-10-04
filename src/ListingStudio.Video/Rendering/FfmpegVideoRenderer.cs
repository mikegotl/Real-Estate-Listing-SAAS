using System.Diagnostics;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Rendering;

public sealed class FfmpegVideoRenderer(
    IOptions<FfmpegOptions> options) : IVideoRenderer
{
    public async Task<VideoRenderResult> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        ValidateConfiguration(configuration);
        var outputPath = Path.GetFullPath(request.OutputFilePath);
        if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The renderer output path must use the .mp4 extension.", nameof(request));
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("The renderer will not overwrite an existing output file.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.rendering.mp4");
        var normalizedRequest = request with { OutputFilePath = temporaryOutputPath };
        var command = FfmpegCommandBuilder.Build(normalizedRequest);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(configuration.ExecutablePath, command.Arguments),
            EnableRaisingEvents = true,
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new VideoRenderException("FFmpeg could not be started.");
            }
        }
        catch (VideoRenderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VideoRenderException("FFmpeg could not be started.", innerException: exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RenderTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException exception)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            DeletePartialOutput(temporaryOutputPath);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new VideoRenderException(
                    $"FFmpeg rendering exceeded the {configuration.RenderTimeoutSeconds}-second timeout.",
                    innerException: exception);
            }

            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        stopwatch.Stop();
        if (process.ExitCode != 0)
        {
            DeletePartialOutput(temporaryOutputPath);
            throw new VideoRenderException(
                $"FFmpeg rendering failed with exit code {process.ExitCode}: {UsefulError(standardError)}",
                process.ExitCode,
                standardError);
        }

        if (!File.Exists(temporaryOutputPath) || new FileInfo(temporaryOutputPath).Length == 0)
        {
            DeletePartialOutput(temporaryOutputPath);
            throw new VideoRenderException(
                "FFmpeg reported success but did not create a playable output candidate.",
                process.ExitCode,
                standardError);
        }

        try
        {
            File.Move(temporaryOutputPath, outputPath, overwrite: false);
        }
        catch (Exception exception)
        {
            DeletePartialOutput(temporaryOutputPath);
            throw new VideoRenderException(
                "FFmpeg rendered successfully, but the completed output could not be published.",
                process.ExitCode,
                standardError,
                exception);
        }

        return new VideoRenderResult(
            outputPath,
            process.ExitCode,
            stopwatch.Elapsed,
            standardOutput,
            standardError);
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ValidateConfiguration(FfmpegOptions configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ExecutablePath);
        if (configuration.RenderTimeoutSeconds is < 1 or > 3_600)
        {
            throw new InvalidOperationException("FFmpeg:RenderTimeoutSeconds must be between 1 and 3600.");
        }
    }

    private static void Kill(Process process)
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
            // The process exited between the state check and kill request.
        }
    }

    private static string UsefulError(string standardError)
    {
        var lines = standardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault() ?? "No diagnostic output was returned.";
    }

    private static void DeletePartialOutput(string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }
}
