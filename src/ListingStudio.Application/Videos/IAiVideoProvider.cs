using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface IAiVideoProvider
{
    bool IsEnabled { get; }

    string GenerationVersion { get; }

    Task<AiVideoProviderResult> GenerateAsync(
        AiVideoProviderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AiVideoProviderRequest(
    Guid PropertyMediaId,
    string OriginalFilename,
    string ContentType,
    Stream Content,
    string MotionInstruction,
    int DurationMs,
    VideoAspectRatio AspectRatio,
    string IdempotencyKey);

public sealed record AiVideoProviderResult(
    Stream Content,
    string ContentType,
    string Provider,
    string Model,
    string? ProviderRequestId,
    int DurationMs,
    int Width,
    int Height,
    decimal? EstimatedCostUsd,
    IReadOnlyDictionary<string, string> Metadata);
