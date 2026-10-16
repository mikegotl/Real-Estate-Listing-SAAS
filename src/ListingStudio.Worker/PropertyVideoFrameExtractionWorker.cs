using ListingStudio.Application.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>
/// Turns videos into room photos when the agent asks. Room recognition uses the media analyzer, so this runs only
/// where media analysis is enabled.
/// </summary>
public sealed partial class PropertyVideoFrameExtractionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaAnalysisWorkerOptions> options,
    ILogger<PropertyVideoFrameExtractionWorker> logger) : BackgroundService
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
                var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoFrameExtractionProcessor>();
                var result = await processor.ProcessNextAsync(stoppingToken);
                if (result?.Succeeded == true)
                {
                    LogCompleted(result.VideoId, result.PhotoCount, result.AttemptNumber);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio video room-photo worker started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Video room photos are disabled because media analysis is disabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Property video {VideoId} gave {PhotoCount} room photos on attempt {AttemptNumber}")]
    private partial void LogCompleted(Guid videoId, int photoCount, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Room photos for property video {VideoId} failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogFailed(Guid videoId, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Video room-photo polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
