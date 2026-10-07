using ListingStudio.Application.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

public sealed partial class PropertyVideoProcessingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<VideoProcessingWorkerOptions> options,
    ILogger<PropertyVideoProcessingWorker> logger) : BackgroundService
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
                var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoProcessingProcessor>();
                var result = await processor.ProcessNextAsync(stoppingToken);
                if (result?.Succeeded == true)
                {
                    LogCompleted(result.VideoId, result.AttemptNumber);
                }
                else if (result is not null)
                {
                    LogFailed(result.VideoId, result.AttemptNumber, result.WillRetry, result.Error);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio property-video processor started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio property-video processing is disabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Property video {VideoId} processing completed on attempt {AttemptNumber}")]
    private partial void LogCompleted(Guid videoId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Property video {VideoId} processing failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogFailed(Guid videoId, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Property-video polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
