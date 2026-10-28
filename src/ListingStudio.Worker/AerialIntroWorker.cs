using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>Submits queued AI drone intros and collects them once the provider finishes.</summary>
public sealed partial class AerialIntroWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<VideoProcessingWorkerOptions> options,
    ILogger<AerialIntroWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        LogStarted();
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAerialIntroProcessor>();
                var result = await processor.ProcessNextAsync(stoppingToken);
                if (result?.Status == AerialIntroClipStatus.Completed)
                {
                    LogCompleted(result.ClipId, result.AttemptNumber);
                }
                else if (result?.Status == AerialIntroClipStatus.Failed)
                {
                    LogFailed(result.ClipId, result.AttemptNumber, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogIterationFailed(exception);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio drone intro processor started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio drone intro processing is disabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Drone intro {ClipId} completed on attempt {AttemptNumber}")]
    private partial void LogCompleted(Guid clipId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drone intro {ClipId} failed on attempt {AttemptNumber}. {Error}")]
    private partial void LogFailed(Guid clipId, int attemptNumber, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Drone intro polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
