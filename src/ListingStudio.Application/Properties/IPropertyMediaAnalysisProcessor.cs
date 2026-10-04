namespace ListingStudio.Application.Properties;

public interface IPropertyMediaAnalysisProcessor
{
    public const int MaximumAttempts = 3;

    Task<PropertyMediaAnalysisRunResult?> AnalyzeNextAsync(CancellationToken cancellationToken = default);
}

public sealed record PropertyMediaAnalysisRunResult(
    Guid MediaId,
    bool Succeeded,
    bool WillRetry,
    int AttemptNumber,
    string? Error);
