namespace ListingStudio.Domain.Properties;

public sealed class PropertyVideo
{
    private PropertyVideo()
    {
    }

    private PropertyVideo(
        Guid organizationId,
        Guid propertyId,
        string originalBlobPath,
        string originalFilename,
        string originalMimeType,
        long originalFileSize,
        int width,
        int height,
        int durationMs,
        decimal frameRate,
        bool hasAudio)
    {
        Id = Guid.NewGuid();
        OrganizationId = RequiredId(organizationId, nameof(organizationId));
        PropertyId = RequiredId(propertyId, nameof(propertyId));
        OriginalBlobPath = Required(originalBlobPath, 1_024, nameof(originalBlobPath));
        OriginalFilename = Required(originalFilename, 255, nameof(originalFilename));
        OriginalMimeType = Required(originalMimeType, 100, nameof(originalMimeType));
        OriginalFileSize = Positive(originalFileSize, nameof(originalFileSize));
        Width = Positive(width, nameof(width));
        Height = Positive(height, nameof(height));
        DurationMs = Positive(durationMs, nameof(durationMs));
        FrameRate = Positive(frameRate, nameof(frameRate));
        HasAudio = hasAudio;
        ProcessingStatus = PropertyVideoProcessingStatus.Pending;
        UploadedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string OriginalBlobPath { get; private set; } = string.Empty;
    public string OriginalFilename { get; private set; } = string.Empty;
    public string OriginalMimeType { get; private set; } = string.Empty;
    public long OriginalFileSize { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DurationMs { get; private set; }
    public decimal FrameRate { get; private set; }
    public bool HasAudio { get; private set; }
    public DateTimeOffset UploadedAtUtc { get; private set; }
    public PropertyVideoProcessingStatus ProcessingStatus { get; private set; }
    public int ProcessingAttemptCount { get; private set; }
    public DateTimeOffset? ProcessingLastAttemptedAtUtc { get; private set; }
    public DateTimeOffset? ProcessingNextAttemptAtUtc { get; private set; }
    public DateTimeOffset? ProcessingCompletedAtUtc { get; private set; }
    public string? ProcessingLastError { get; private set; }
    public string? EnhancedBlobPath { get; private set; }
    public long? EnhancedFileSize { get; private set; }
    public int? EnhancedWidth { get; private set; }
    public int? EnhancedHeight { get; private set; }
    public int? EnhancedDurationMs { get; private set; }
    public decimal? EnhancedFrameRate { get; private set; }
    public string? EnhancementVersion { get; private set; }
    public ListingProperty Property { get; private set; } = null!;

    public static PropertyVideo Create(
        Guid organizationId,
        Guid propertyId,
        string originalBlobPath,
        string originalFilename,
        string originalMimeType,
        long originalFileSize,
        int width,
        int height,
        int durationMs,
        decimal frameRate,
        bool hasAudio) => new(
            organizationId,
            propertyId,
            originalBlobPath,
            originalFilename,
            originalMimeType,
            originalFileSize,
            width,
            height,
            durationMs,
            frameRate,
            hasAudio);

    public void BeginProcessing(DateTimeOffset attemptedAtUtc)
    {
        if (ProcessingStatus == PropertyVideoProcessingStatus.Completed)
        {
            throw new InvalidOperationException("Completed video processing cannot be restarted without a new upload.");
        }

        ProcessingStatus = PropertyVideoProcessingStatus.Processing;
        ProcessingAttemptCount++;
        ProcessingLastAttemptedAtUtc = attemptedAtUtc;
        ProcessingNextAttemptAtUtc = null;
        ProcessingLastError = null;
    }

    public void CompleteProcessing(
        string enhancedBlobPath,
        long enhancedFileSize,
        int enhancedWidth,
        int enhancedHeight,
        int enhancedDurationMs,
        decimal enhancedFrameRate,
        string enhancementVersion,
        DateTimeOffset completedAtUtc)
    {
        if (ProcessingStatus != PropertyVideoProcessingStatus.Processing)
        {
            throw new InvalidOperationException("Video processing must be in progress before it can be completed.");
        }

        EnhancedBlobPath = Required(enhancedBlobPath, 1_024, nameof(enhancedBlobPath));
        EnhancedFileSize = Positive(enhancedFileSize, nameof(enhancedFileSize));
        EnhancedWidth = Positive(enhancedWidth, nameof(enhancedWidth));
        EnhancedHeight = Positive(enhancedHeight, nameof(enhancedHeight));
        EnhancedDurationMs = Positive(enhancedDurationMs, nameof(enhancedDurationMs));
        EnhancedFrameRate = Positive(enhancedFrameRate, nameof(enhancedFrameRate));
        EnhancementVersion = Required(enhancementVersion, 100, nameof(enhancementVersion));
        ProcessingCompletedAtUtc = completedAtUtc;
        ProcessingNextAttemptAtUtc = null;
        ProcessingLastError = null;
        ProcessingStatus = PropertyVideoProcessingStatus.Completed;
    }

    public void FailProcessing(string error, DateTimeOffset? nextAttemptAtUtc)
    {
        if (ProcessingStatus != PropertyVideoProcessingStatus.Processing)
        {
            throw new InvalidOperationException("Video processing must be in progress before it can fail.");
        }

        ProcessingLastError = Required(error, 1_000, nameof(error));
        ProcessingNextAttemptAtUtc = nextAttemptAtUtc;
        ProcessingStatus = PropertyVideoProcessingStatus.Failed;
    }

    public void QueueRetry()
    {
        if (ProcessingStatus != PropertyVideoProcessingStatus.Failed)
        {
            throw new InvalidOperationException("Only failed video processing can be retried.");
        }

        ProcessingStatus = PropertyVideoProcessingStatus.Pending;
        ProcessingAttemptCount = 0;
        ProcessingLastAttemptedAtUtc = null;
        ProcessingNextAttemptAtUtc = null;
        ProcessingLastError = null;
    }

    private static Guid RequiredId(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("An identity is required.", name) : value;

    private static T Positive<T>(T value, string name) where T : struct, IComparable<T> =>
        value.CompareTo(default) <= 0 ? throw new ArgumentOutOfRangeException(name, "Value must be positive.") : value;

    private static string Required(string value, int maximumLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(name, $"Value cannot exceed {maximumLength} characters.");
    }
}
