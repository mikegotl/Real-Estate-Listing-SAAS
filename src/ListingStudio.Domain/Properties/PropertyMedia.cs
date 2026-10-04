namespace ListingStudio.Domain.Properties;

public sealed class PropertyMedia
{
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
