namespace ListingStudio.Domain.Properties;

public sealed class PropertyMedia
{
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
