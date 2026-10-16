namespace ListingStudio.Application.Properties;

public interface IPropertyVideoFrameExtractionProcessor
{
    public const int MaximumAttempts = 3;

    /// <summary>Most stills sent for room recognition per video, which bounds the AI cost of one extraction.</summary>
    public const int MaximumCandidateFrames = 24;

    Task<PropertyVideoFrameExtractionRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed record PropertyVideoFrameExtractionRunResult(
    Guid VideoId,
    bool Succeeded,
    bool WillRetry,
    int AttemptNumber,
    int PhotoCount,
    string? Error);
