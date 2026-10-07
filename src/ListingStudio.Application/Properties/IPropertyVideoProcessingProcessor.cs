namespace ListingStudio.Application.Properties;

public interface IPropertyVideoProcessingProcessor
{
    const int MaximumAttempts = 3;

    Task<PropertyVideoProcessingRunResult?> ProcessNextAsync(
        CancellationToken cancellationToken = default);
}

public sealed record PropertyVideoProcessingRunResult(
    Guid VideoId,
    bool Succeeded,
    bool WillRetry,
    int AttemptNumber,
    string? Error);
