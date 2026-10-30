using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>
/// Makes the fixed photos agents ask for on the Media tab. Brightness and color needs nothing beyond FFmpeg, so this
/// always runs; Remove clutter requests only exist where the AI edit is switched on.
/// </summary>
public sealed partial class PhotoFixWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PhotoFixWorkerOptions> options,
    ILogger<PhotoFixWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            PropertyPhotoFixRunResult? result = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixProcessor>();
                result = await processor.ProcessNextAsync(stoppingToken);
                if (result?.Succeeded == true)
                {
                    LogCompleted(result.FixId, result.Kind, result.PropertyMediaId, result.AttemptNumber);
                }
                else if (result is not null)
                {
                    LogFailed(result.FixId, result.Kind, result.AttemptNumber, result.WillRetry, result.Error);
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

            // Keep going straight to the next fix while there is a queue.
            if (result is not null)
            {
                continue;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Creative Listing Studio photo fix worker started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Photo fix {FixId} ({Kind}) replaced photo {MediaId} on attempt {AttemptNumber}")]
    private partial void LogCompleted(Guid fixId, PhotoFix kind, Guid mediaId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Photo fix {FixId} ({Kind}) failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogFailed(Guid fixId, PhotoFix kind, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Photo fix polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
