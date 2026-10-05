using ListingStudio.Application.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

public sealed partial class Worker(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaAnalysisWorkerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogMediaAnalysisDisabled();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        LogWorkerStarted();
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPropertyMediaAnalysisProcessor>();
                var result = await processor.AnalyzeNextAsync(stoppingToken);
                if (result is null)
                {
                    LogNoPendingMedia();
                }
                else if (result.Succeeded)
                {
                    LogAnalysisCompleted(result.MediaId, result.AttemptNumber);
                }
                else
                {
                    LogAnalysisFailed(result.MediaId, result.AttemptNumber, result.WillRetry, result.Error);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio media-analysis worker started")]
    private partial void LogWorkerStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio media analysis is disabled")]
    private partial void LogMediaAnalysisDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "No property media is ready for analysis")]
    private partial void LogNoPendingMedia();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Property media {MediaId} analysis completed on attempt {AttemptNumber}")]
    private partial void LogAnalysisCompleted(Guid mediaId, int attemptNumber);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Property media {MediaId} analysis failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogAnalysisFailed(Guid mediaId, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Property-media polling failed; the worker will retry after the configured interval")]
    private partial void LogIterationFailed(Exception exception);
}
