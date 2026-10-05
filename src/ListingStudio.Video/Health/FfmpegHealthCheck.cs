namespace ListingStudio.Video.Health;

using System.Diagnostics;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

public sealed class FfmpegHealthCheck(IOptions<FfmpegOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg availability check could not start the process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return HealthCheckResult.Unhealthy("FFmpeg availability check timed out.");
        }

        return process.ExitCode == 0
            ? HealthCheckResult.Healthy("FFmpeg is available.")
            : HealthCheckResult.Unhealthy($"FFmpeg exited with code {process.ExitCode}.");
    }
}
