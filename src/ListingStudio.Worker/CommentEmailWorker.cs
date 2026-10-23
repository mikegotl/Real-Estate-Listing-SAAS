using ListingStudio.Application.Feedback;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

/// <summary>
/// Emails each user comment to the platform admins. Nothing is sent until an SMTP host and sender address are
/// configured; comments saved before then are emailed once it is.
/// </summary>
public sealed partial class CommentEmailWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CommentEmailWorkerOptions> options,
    ILogger<CommentEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            FeatureSuggestionEmailRunResult? result = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IFeatureSuggestionEmailProcessor>();
                result = await processor.ProcessNextAsync(stoppingToken);
                switch (result?.Outcome)
                {
                    case FeatureSuggestionEmailOutcome.Sent:
                        LogSent(result.SuggestionId, result.RecipientCount);
                        break;
                    case FeatureSuggestionEmailOutcome.NoRecipients:
                        LogNoRecipients(result.SuggestionId);
                        break;
                    case FeatureSuggestionEmailOutcome.Failed:
                        LogFailed(result.SuggestionId, result.AttemptNumber, result.WillRetry, result.Error);
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

            // Keep going while comments are waiting; a failed or deferred one has moved its next attempt later.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio comment email worker started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Comment {SuggestionId} emailed to {RecipientCount} platform admins")]
    private partial void LogSent(Guid suggestionId, int recipientCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comment {SuggestionId} was not emailed: no platform admin has an email address. Checking again in an hour")]
    private partial void LogNoRecipients(Guid suggestionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Emailing comment {SuggestionId} failed on attempt {AttemptNumber}; retry scheduled: {WillRetry}. {Error}")]
    private partial void LogFailed(Guid suggestionId, int attemptNumber, bool willRetry, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Comment email polling failed; the worker will retry")]
    private partial void LogIterationFailed(Exception exception);
}
