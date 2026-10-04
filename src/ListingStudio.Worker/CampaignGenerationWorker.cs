using ListingStudio.Application.Campaigns;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

public sealed partial class CampaignGenerationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CampaignGenerationOptions> options,
    ILogger<CampaignGenerationWorker> logger) : BackgroundService
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>();
            var result = await processor.ProcessNextStageAsync(stoppingToken);
            if (result is not null)
            {
                LogStageProcessed(
                    result.JobId,
                    result.Stage,
                    result.Succeeded,
                    result.StageCompleted,
                    result.WillRetry,
                    result.AttemptNumber);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio campaign-generation worker started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio campaign generation is disabled")]
    private partial void LogDisabled();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} stage {Stage} processed; success {Succeeded}, complete {StageCompleted}, retry {WillRetry}, attempt {AttemptNumber}")]
    private partial void LogStageProcessed(
        Guid jobId,
        Domain.Campaigns.CampaignGenerationStage stage,
        bool succeeded,
        bool stageCompleted,
        bool willRetry,
        int attemptNumber);
}
