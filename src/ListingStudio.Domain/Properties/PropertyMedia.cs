namespace ListingStudio.Domain.Properties;

public sealed class PropertyMedia
{
    public const int MaximumWalkthroughStopLength = 80;

    private string[] potentialProblems = [];

    private PropertyMedia()
    {
    }

    private PropertyMedia(
        Guid organizationId,
        Guid propertyId,
        string blobPath,
        string originalFilename,
        string mimeType,
        long fileSize,
        int width,
        int height,
        int displayOrder)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        BlobPath = Required(blobPath, 1_024, nameof(blobPath));
        OriginalFilename = Required(originalFilename, 255, nameof(originalFilename));
        MimeType = Required(mimeType, 100, nameof(mimeType));

        if (fileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSize), "File size must be positive.");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Image height must be positive.");
        }

        if (displayOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayOrder), "Display order cannot be negative.");
        }

        FileSize = fileSize;
        Width = width;
        Height = height;
        DisplayOrder = displayOrder;
        UploadedAt = DateTimeOffset.UtcNow;
        AnalysisStatus = PropertyMediaAnalysisStatus.Pending;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string BlobPath { get; private set; } = string.Empty;

    public string OriginalFilename { get; private set; } = string.Empty;

    public string MimeType { get; private set; } = string.Empty;

    public long FileSize { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int DisplayOrder { get; private set; }

    // The agent's walkthrough storyboard stop, such as "Exterior front" or "Kitchen". Null for photos added before
    // the storyboard existed; those keep their display order until the agent places them.
    public string? WalkthroughStop { get; private set; }

    // True while the photo waits in the photo gallery: it stays with the property but is left out of the storyboard,
    // the walkthrough map, walk-through clips, the story and the video until the agent places it in a stop.
    public bool IsInGallery { get; private set; }

    // Set for a photo taken from one of the property's videos: the video and where in it the frame was captured.
    public Guid? SourceVideoId { get; private set; }

    public int? SourceVideoTimestampMs { get; private set; }

    // Set once a photo fix replaces the photo: the upload as the agent first added it, kept so the agent can compare
    // the fixed photo with it and revert. Everything else (storyboard, map, clips, story, video) uses BlobPath.
    public string? OriginalBlobPath { get; private set; }

    public string? OriginalMimeType { get; private set; }

    public long? OriginalFileSize { get; private set; }

    public int? OriginalWidth { get; private set; }

    public int? OriginalHeight { get; private set; }

    public PhotoFix AppliedFixes { get; private set; }

    public DateTimeOffset? EditedAtUtc { get; private set; }

    public bool IsEdited => OriginalBlobPath is not null;

    public DateTimeOffset UploadedAt { get; private set; }

    public PropertyMediaAnalysisStatus AnalysisStatus { get; private set; }

    public PropertyMediaCategory? Category { get; private set; }

    public string? RoomType { get; private set; }

    public int? QualityScore { get; private set; }

    public int? HeroScore { get; private set; }

    public bool? IsExterior { get; private set; }

    public bool? IsInterior { get; private set; }

    public bool? ContainsPeople { get; private set; }

    public IReadOnlyList<string> PotentialProblems => potentialProblems;

    public string? AnalysisDescription { get; private set; }

    public int? SuggestedDisplayOrder { get; private set; }

    public int AnalysisAttemptCount { get; private set; }

    public DateTimeOffset? AnalysisLastAttemptedAtUtc { get; private set; }

    public DateTimeOffset? AnalysisNextAttemptAtUtc { get; private set; }

    public DateTimeOffset? AnalysisCompletedAtUtc { get; private set; }

    public string? AnalysisLastError { get; private set; }

    public ListingProperty Property { get; private set; } = null!;

    public static PropertyMedia Create(
        Guid organizationId,
        Guid propertyId,
        string blobPath,
        string originalFilename,
        string mimeType,
        long fileSize,
        int width,
        int height,
        int displayOrder)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("A property is required.", nameof(propertyId));
        }

        return new PropertyMedia(
            organizationId,
            propertyId,
            blobPath,
            originalFilename,
            mimeType,
            fileSize,
            width,
            height,
            displayOrder);
    }

    public void SetDisplayOrder(int displayOrder)
    {
        if (displayOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayOrder), "Display order cannot be negative.");
        }

        DisplayOrder = displayOrder;
    }

    /// <summary>Places the photo in a storyboard stop. Placing a gallery photo takes it out of the gallery.</summary>
    public void SetWalkthroughStop(string? walkthroughStop)
    {
        WalkthroughStop = string.IsNullOrWhiteSpace(walkthroughStop)
            ? null
            : Required(walkthroughStop, MaximumWalkthroughStopLength, nameof(walkthroughStop));
        if (WalkthroughStop is not null)
        {
            IsInGallery = false;
        }
    }

    /// <summary>Holds the photo back in the gallery, out of its stop and out of the walkthrough.</summary>
    public void MoveToGallery()
    {
        WalkthroughStop = null;
        IsInGallery = true;
    }

    public void SetSourceVideoFrame(Guid videoId, int timestampMs)
    {
        if (videoId == Guid.Empty)
        {
            throw new ArgumentException("A source video is required.", nameof(videoId));
        }

        if (timestampMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMs), "A frame timestamp cannot be negative.");
        }

        SourceVideoId = videoId;
        SourceVideoTimestampMs = timestampMs;
    }

    /// <summary>
    /// Replaces the photo with a fixed version. The first fix keeps the upload as the original; a later fix builds on
    /// the current photo. Returns the blob of the previous fixed version, which is no longer needed, or null.
    /// The photo is analyzed again because its content changed.
    /// </summary>
    public string? ApplyFix(
        PhotoFix fix,
        string blobPath,
        string mimeType,
        long fileSize,
        int width,
        int height,
        DateTimeOffset editedAtUtc)
    {
        if (fix is not (PhotoFix.BrightnessAndColor or PhotoFix.RemoveClutter))
        {
            throw new ArgumentOutOfRangeException(nameof(fix), "Exactly one photo fix is applied at a time.");
        }

        var normalizedBlobPath = Required(blobPath, 1_024, nameof(blobPath));
        if (normalizedBlobPath == BlobPath || normalizedBlobPath == OriginalBlobPath)
        {
            throw new ArgumentException("A fixed photo needs its own stored file.", nameof(blobPath));
        }

        var normalizedMimeType = Required(mimeType, 100, nameof(mimeType));
        if (fileSize <= 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSize), "A fixed photo needs a size and dimensions.");
        }

        string? superseded = null;
        if (OriginalBlobPath is null)
        {
            OriginalBlobPath = BlobPath;
            OriginalMimeType = MimeType;
            OriginalFileSize = FileSize;
            OriginalWidth = Width;
            OriginalHeight = Height;
        }
        else
        {
            superseded = BlobPath;
        }

        BlobPath = normalizedBlobPath;
        MimeType = normalizedMimeType;
        FileSize = fileSize;
        Width = width;
        Height = height;
        AppliedFixes |= fix;
        EditedAtUtc = editedAtUtc;
        RestartAnalysisForNewContent();
        return superseded;
    }

    /// <summary>
    /// Puts the original upload back. Returns the blob of the fixed version, which is no longer needed.
    /// </summary>
    public string RevertToOriginal(DateTimeOffset revertedAtUtc)
    {
        if (OriginalBlobPath is null)
        {
            throw new InvalidOperationException("The photo has not been fixed, so there is nothing to revert.");
        }

        var fixedBlobPath = BlobPath;
        BlobPath = OriginalBlobPath;
        MimeType = OriginalMimeType!;
        FileSize = OriginalFileSize!.Value;
        Width = OriginalWidth!.Value;
        Height = OriginalHeight!.Value;
        OriginalBlobPath = null;
        OriginalMimeType = null;
        OriginalFileSize = null;
        OriginalWidth = null;
        OriginalHeight = null;
        AppliedFixes = PhotoFix.None;
        EditedAtUtc = revertedAtUtc;
        RestartAnalysisForNewContent();
        return fixedBlobPath;
    }

    private void RestartAnalysisForNewContent()
    {
        potentialProblems = [];
        Category = null;
        RoomType = null;
        QualityScore = null;
        HeroScore = null;
        IsExterior = null;
        IsInterior = null;
        ContainsPeople = null;
        AnalysisDescription = null;
        SuggestedDisplayOrder = null;
        AnalysisCompletedAtUtc = null;
        AnalysisAttemptCount = 0;
        AnalysisLastAttemptedAtUtc = null;
        AnalysisNextAttemptAtUtc = null;
        AnalysisLastError = null;
        AnalysisStatus = PropertyMediaAnalysisStatus.Pending;
    }

    public void BeginAnalysis(DateTimeOffset attemptedAtUtc)
    {
        if (AnalysisStatus == PropertyMediaAnalysisStatus.Completed)
        {
            throw new InvalidOperationException("Completed media analysis cannot be restarted without an explicit retry.");
        }

        AnalysisStatus = PropertyMediaAnalysisStatus.Analyzing;
        AnalysisAttemptCount++;
        AnalysisLastAttemptedAtUtc = attemptedAtUtc;
        AnalysisNextAttemptAtUtc = null;
        AnalysisLastError = null;
    }

    public void CompleteAnalysis(PropertyMediaAnalysis analysis, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (AnalysisStatus != PropertyMediaAnalysisStatus.Analyzing)
        {
            throw new InvalidOperationException("Media analysis must be in progress before it can be completed.");
        }

        if (analysis.QualityScore is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(analysis), "Quality score must be between 0 and 100.");
        }

        if (analysis.HeroScore is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(analysis), "Hero score must be between 0 and 100.");
        }

        if (analysis.IsExterior && analysis.IsInterior)
        {
            throw new ArgumentException("An image cannot be both interior and exterior.", nameof(analysis));
        }

        if (analysis.SuggestedDisplayOrder is < 0 or >= 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysis),
                "Suggested display order must be between 0 and 49.");
        }

        if (!Enum.IsDefined(analysis.Category))
        {
            throw new ArgumentOutOfRangeException(nameof(analysis), "Media category is not supported.");
        }

        var problems = analysis.PotentialProblems ?? throw new ArgumentException(
            "Potential problems are required.",
            nameof(analysis));
        if (problems.Count > 20)
        {
            throw new ArgumentException("No more than 20 potential problems may be recorded.", nameof(analysis));
        }

        var normalizedProblems = problems.Select(problem => Required(problem, 500, nameof(analysis))).ToArray();
        var normalizedRoomType = Required(analysis.RoomType, 100, nameof(analysis));
        var normalizedDescription = Required(analysis.Description, 2_000, nameof(analysis));

        potentialProblems = normalizedProblems;
        Category = analysis.Category;
        RoomType = normalizedRoomType;
        QualityScore = analysis.QualityScore;
        HeroScore = analysis.HeroScore;
        IsExterior = analysis.IsExterior;
        IsInterior = analysis.IsInterior;
        ContainsPeople = analysis.ContainsPeople;
        AnalysisDescription = normalizedDescription;
        SuggestedDisplayOrder = analysis.SuggestedDisplayOrder;
        AnalysisCompletedAtUtc = completedAtUtc;
        AnalysisNextAttemptAtUtc = null;
        AnalysisLastError = null;
        AnalysisStatus = PropertyMediaAnalysisStatus.Completed;
    }

    public void FailAnalysis(string error, DateTimeOffset? nextAttemptAtUtc)
    {
        if (AnalysisStatus != PropertyMediaAnalysisStatus.Analyzing)
        {
            throw new InvalidOperationException("Media analysis must be in progress before it can fail.");
        }

        AnalysisLastError = Required(error, 1_000, nameof(error));
        AnalysisNextAttemptAtUtc = nextAttemptAtUtc;
        AnalysisStatus = PropertyMediaAnalysisStatus.Failed;
    }

    public void QueueAnalysisRetry()
    {
        if (AnalysisStatus != PropertyMediaAnalysisStatus.Failed)
        {
            throw new InvalidOperationException("Only failed media analysis can be retried.");
        }

        AnalysisStatus = PropertyMediaAnalysisStatus.Pending;
        AnalysisAttemptCount = 0;
        AnalysisLastAttemptedAtUtc = null;
        AnalysisNextAttemptAtUtc = null;
        AnalysisLastError = null;
    }

    public PropertyMediaAnalysis? GetAnalysis() => AnalysisStatus == PropertyMediaAnalysisStatus.Completed
        ? new PropertyMediaAnalysis(
            Category!.Value,
            RoomType!,
            QualityScore!.Value,
            HeroScore!.Value,
            IsExterior!.Value,
            IsInterior!.Value,
            ContainsPeople!.Value,
            potentialProblems,
            AnalysisDescription!,
            SuggestedDisplayOrder!.Value)
        : null;

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }
}
