using ListingStudio.Application.Campaigns;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Worker;

public sealed partial class TikTokTestSuiteWorker(IServiceScopeFactory scopeFactory,
    IOptions<CampaignGenerationOptions> options, ILogger<TikTokTestSuiteWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteProcessor>().ProcessNextAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                LogIterationFailed(exception);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "TikTok suite polling failed; the durable queue will be retried")]
    private partial void LogIterationFailed(Exception exception);
}
