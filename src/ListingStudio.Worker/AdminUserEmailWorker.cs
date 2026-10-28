using ListingStudio.Application.Administration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>
/// Sends the emails platform admins write to users from Find users. Nothing is sent until an SMTP host and sender
/// address are configured; emails queued before then go out once it is. Uses the comment email poll interval.
/// </summary>
public sealed partial class AdminUserEmailWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CommentEmailWorkerOptions> options,
    ILogger<AdminUserEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            AdminUserEmailRunResult? result = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAdminUserEmailProcessor>();
                result = await processor.ProcessNextAsync(stoppingToken);
                switch (result?.Outcome)
                {
                    case AdminUserEmailSendOutcome.Sent:
                        LogSent(result.EmailId);
                        break;
                    case AdminUserEmailSendOutcome.Failed:
                        LogFailed(result.EmailId, result.AttemptNumber, result.WillRetry, result.Error);
                        break;
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

            // Keep going while emails are waiting; a failed one has moved its next attempt later.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio admin user email worker started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin email {EmailId} sent")]
    private partial void LogSent(Guid emailId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending admin email {EmailId} failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogFailed(Guid emailId, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Admin email polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
