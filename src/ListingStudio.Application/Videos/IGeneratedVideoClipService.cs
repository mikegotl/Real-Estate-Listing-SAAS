using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface IGeneratedVideoClipService
{
    bool IsEnabled { get; }

    Task<GeneratedVideoClipResult?> GetOrCreateAsync(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        string motionInstruction,
        int durationMs,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default);
}

public sealed record GeneratedVideoClipResult(
    Guid Id,
    Guid PropertyMediaId,
    int DurationMs,
    VideoAspectRatio AspectRatio,
    int Width,
    int Height,
    string Provider,
    string Model,
    decimal? EstimatedCostUsd,
    bool Reused);
