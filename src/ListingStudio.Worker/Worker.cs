namespace ListingStudio.Worker;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Listing Studio worker started");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
