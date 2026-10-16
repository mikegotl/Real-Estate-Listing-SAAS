using System.Diagnostics;

namespace ListingStudio.Video.Processing;

/// <summary>Runs FFmpeg or FFprobe with a timeout, failing on a non-zero exit with the tail of its error output.</summary>
internal static class FfmpegProcess
{
    public static async Task<FfmpegProcessOutput> RunAsync(
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

        return new FfmpegProcessOutput(output, error);
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
}

internal sealed record FfmpegProcessOutput(string StandardOutput, string StandardError);
