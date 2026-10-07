namespace ListingStudio.Application.Properties;

public interface IPropertyVideoTranscoder
{
    string EnhancementVersion { get; }

    Task<PropertyVideoMetadata> ProbeAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default);

    Task<PropertyVideoEnhancementResult> EnhanceAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyVideoMetadata(
    string ContainerFormat,
    int Width,
    int Height,
    int DurationMs,
    decimal FrameRate,
    bool HasAudio);

public sealed record PropertyVideoEnhancementResult(
    Stream Content,
    long FileSize,
    PropertyVideoMetadata Metadata);
