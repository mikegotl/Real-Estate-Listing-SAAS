using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

public interface IPropertyVideoService
{
    const int MaximumVideosPerProperty = 10;
    const long MaximumFileSize = 500L * 1024 * 1024;
    const int MaximumDurationMs = 5 * 60 * 1_000;
    const int MaximumDimension = 4_096;

    Task<IReadOnlyList<PropertyVideoItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyVideoUpload upload,
        CancellationToken cancellationToken = default);

    Task<PropertyVideoContent?> OpenReadAsync(
        string userId,
        Guid videoId,
        bool enhanced,
        CancellationToken cancellationToken = default);

    Task<bool> RetryProcessingAsync(
        string userId,
        Guid propertyId,
        Guid videoId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid videoId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyVideoUpload(
    string OriginalFilename,
    string ContentType,
    long FileSize,
    Stream Content);

public sealed record PropertyVideoItem(
    Guid Id,
    string OriginalFilename,
    string OriginalMimeType,
    long OriginalFileSize,
    int Width,
    int Height,
    int DurationMs,
    decimal FrameRate,
    bool HasAudio,
    DateTimeOffset UploadedAtUtc,
    PropertyVideoProcessingStatus ProcessingStatus,
    int ProcessingAttemptCount,
    string? ProcessingLastError,
    long? EnhancedFileSize,
    int? EnhancedWidth,
    int? EnhancedHeight,
    int? EnhancedDurationMs,
    decimal? EnhancedFrameRate,
    string? EnhancementVersion);

public sealed record PropertyVideoContent(Stream Content, string MimeType, string Filename);
