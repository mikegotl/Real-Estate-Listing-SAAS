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

    Task<bool> SetTourStopAsync(
        string userId,
        Guid propertyId,
        Guid videoId,
        PropertyMediaCategory? tourStop,
        CancellationToken cancellationToken = default);

    Task<bool> ReorderTourAsync(
        string userId,
        Guid propertyId,
        IReadOnlyList<Guid> orderedVideoIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues the video to be broken down into one or two photos per room it shows. They are added to the property's
    /// photo gallery, ready to place in the storyboard.
    /// </summary>
    Task<bool> RequestFrameExtractionAsync(
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
    int TourOrder,
    PropertyMediaCategory? TourStop,
    DateTimeOffset UploadedAtUtc,
    PropertyVideoProcessingStatus ProcessingStatus,
    int ProcessingAttemptCount,
    string? ProcessingLastError,
    long? EnhancedFileSize,
    int? EnhancedWidth,
    int? EnhancedHeight,
    int? EnhancedDurationMs,
    decimal? EnhancedFrameRate,
    string? EnhancementVersion,
    PropertyVideoFrameExtractionItem? FrameExtraction = null);

public sealed record PropertyVideoFrameExtractionItem(
    PropertyVideoFrameExtractionStatus Status,
    PropertyVideoFrameExtractionStage? Stage,
    int StepsDone,
    int StepsTotal,
    int AttemptCount,
    string? LastError,
    int? RoomCount,
    int? PhotoCount,
    int? SkippedPhotoCount)
{
    public bool IsInProgress => Status is PropertyVideoFrameExtractionStatus.Pending
        or PropertyVideoFrameExtractionStatus.Extracting;
}

public sealed record PropertyVideoContent(Stream Content, string MimeType, string Filename);
