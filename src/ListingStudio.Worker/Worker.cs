namespace ListingStudio.Worker;

public sealed partial class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted();
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listing Studio worker started")]
    private partial void LogWorkerStarted();
}
