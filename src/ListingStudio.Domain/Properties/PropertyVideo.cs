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
        bool hasAudio,
        int tourOrder)
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
        SetTourOrder(tourOrder);
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
    public int TourOrder { get; private set; }
    public PropertyMediaCategory? TourStop { get; private set; }
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

    // Room photos: the agent can ask for the video to be broken down into one or two photos per room it shows,
    // which land in the property's photo gallery.
    public PropertyVideoFrameExtractionStatus FrameExtractionStatus { get; private set; }
    public PropertyVideoFrameExtractionStage? FrameExtractionStage { get; private set; }
    public int FrameExtractionStepsDone { get; private set; }
    public int FrameExtractionStepsTotal { get; private set; }
    public int FrameExtractionAttemptCount { get; private set; }
    public DateTimeOffset? FrameExtractionRequestedAtUtc { get; private set; }
    public DateTimeOffset? FrameExtractionLastAttemptedAtUtc { get; private set; }
    public DateTimeOffset? FrameExtractionNextAttemptAtUtc { get; private set; }
    public DateTimeOffset? FrameExtractionCompletedAtUtc { get; private set; }
    public string? FrameExtractionLastError { get; private set; }
    public int? FrameExtractionRoomCount { get; private set; }
    public int? FrameExtractionPhotoCount { get; private set; }
    public int? FrameExtractionSkippedPhotoCount { get; private set; }
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
        bool hasAudio,
        int tourOrder = 0) => new(
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
            hasAudio,
            tourOrder);

    public void SetTourOrder(int tourOrder)
    {
        if (tourOrder is < 0 or >= 10)
        {
            throw new ArgumentOutOfRangeException(nameof(tourOrder));
        }

        TourOrder = tourOrder;
    }

    public void SetTourStop(PropertyMediaCategory? tourStop)
    {
        if (tourStop is not null && !Enum.IsDefined(tourStop.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(tourStop));
        }

        TourStop = tourStop;
    }

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

    public bool IsFrameExtractionInProgress => FrameExtractionStatus
        is PropertyVideoFrameExtractionStatus.Pending
        or PropertyVideoFrameExtractionStatus.Extracting;

    /// <summary>
    /// Queues a room-photo extraction. A finished or failed extraction can be requested again; frames it already
    /// added are not added twice.
    /// </summary>
    public void RequestFrameExtraction(DateTimeOffset requestedAtUtc)
    {
        if (IsFrameExtractionInProgress)
        {
            throw new InvalidOperationException("Room photos are already being found for this video.");
        }

        FrameExtractionStatus = PropertyVideoFrameExtractionStatus.Pending;
        FrameExtractionStage = null;
        FrameExtractionStepsDone = 0;
        FrameExtractionStepsTotal = 0;
        FrameExtractionAttemptCount = 0;
        FrameExtractionRequestedAtUtc = requestedAtUtc;
        FrameExtractionLastAttemptedAtUtc = null;
        FrameExtractionNextAttemptAtUtc = null;
        FrameExtractionCompletedAtUtc = null;
        FrameExtractionLastError = null;
        FrameExtractionRoomCount = null;
        FrameExtractionPhotoCount = null;
        FrameExtractionSkippedPhotoCount = null;
    }

    public void BeginFrameExtraction(DateTimeOffset attemptedAtUtc)
    {
        if (FrameExtractionStatus is PropertyVideoFrameExtractionStatus.NotRequested
            or PropertyVideoFrameExtractionStatus.Completed)
        {
            throw new InvalidOperationException("Room-photo extraction must be requested before it can start.");
        }

        FrameExtractionStatus = PropertyVideoFrameExtractionStatus.Extracting;
        FrameExtractionStage = PropertyVideoFrameExtractionStage.FindingScenes;
        FrameExtractionStepsDone = 0;
        FrameExtractionStepsTotal = 0;
        FrameExtractionAttemptCount++;
        FrameExtractionLastAttemptedAtUtc = attemptedAtUtc;
        FrameExtractionNextAttemptAtUtc = null;
        FrameExtractionLastError = null;
    }

    public void ReportFrameExtractionProgress(
        PropertyVideoFrameExtractionStage stage,
        int stepsDone,
        int stepsTotal,
        DateTimeOffset reportedAtUtc)
    {
        EnsureExtracting();
        if (!Enum.IsDefined(stage) || stepsTotal < 0 || stepsDone < 0 || stepsDone > stepsTotal)
        {
            throw new ArgumentOutOfRangeException(nameof(stepsDone), "Progress must be within its total.");
        }

        FrameExtractionStage = stage;
        FrameExtractionStepsDone = stepsDone;
        FrameExtractionStepsTotal = stepsTotal;
        // Progress renews the lease, so a long video is not mistaken for an abandoned run.
        FrameExtractionLastAttemptedAtUtc = reportedAtUtc;
    }

    public void CompleteFrameExtraction(int roomCount, int photoCount, int skippedPhotoCount, DateTimeOffset completedAtUtc)
    {
        EnsureExtracting();
        if (roomCount < 0 || photoCount < 0 || skippedPhotoCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(photoCount), "Counts cannot be negative.");
        }

        FrameExtractionStatus = PropertyVideoFrameExtractionStatus.Completed;
        FrameExtractionStage = null;
        FrameExtractionRoomCount = roomCount;
        FrameExtractionPhotoCount = photoCount;
        FrameExtractionSkippedPhotoCount = skippedPhotoCount;
        FrameExtractionCompletedAtUtc = completedAtUtc;
        FrameExtractionNextAttemptAtUtc = null;
        FrameExtractionLastError = null;
    }

    public void FailFrameExtraction(string error, DateTimeOffset? nextAttemptAtUtc)
    {
        EnsureExtracting();
        FrameExtractionLastError = Required(error, 1_000, nameof(error));
        FrameExtractionNextAttemptAtUtc = nextAttemptAtUtc;
        FrameExtractionStatus = PropertyVideoFrameExtractionStatus.Failed;
        FrameExtractionStage = null;
    }

    private void EnsureExtracting()
    {
        if (FrameExtractionStatus != PropertyVideoFrameExtractionStatus.Extracting)
        {
            throw new InvalidOperationException("Room-photo extraction is not in progress.");
        }
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
