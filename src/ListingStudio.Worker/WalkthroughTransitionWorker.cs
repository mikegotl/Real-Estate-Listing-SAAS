using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>Submits queued storyboard walk-through clips and collects them once the provider finishes.</summary>
public sealed partial class WalkthroughTransitionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<VideoProcessingWorkerOptions> options,
    ILogger<WalkthroughTransitionWorker> logger) : BackgroundService
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
                var processor = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionProcessor>();
                var result = await processor.ProcessNextAsync(stoppingToken);
                if (result?.Status == WalkthroughTransitionStatus.Completed)
                {
                    LogCompleted(result.TransitionId, result.AttemptNumber);
                }
                else if (result?.Status == WalkthroughTransitionStatus.Failed)
                {
                    LogFailed(result.TransitionId, result.AttemptNumber, result.Error);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Creative Listing Studio walk-through clip processor started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Creative Listing Studio walk-through clip processing is disabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Walk-through transition {TransitionId} completed on attempt {AttemptNumber}")]
    private partial void LogCompleted(Guid transitionId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through transition {TransitionId} failed on attempt {AttemptNumber}. {Error}")]
    private partial void LogFailed(Guid transitionId, int attemptNumber, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Walk-through clip polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
