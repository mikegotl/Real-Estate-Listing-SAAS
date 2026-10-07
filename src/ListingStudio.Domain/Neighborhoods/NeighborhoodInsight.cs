using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Neighborhoods;

public enum NeighborhoodPlaceCategory
{
    School,
    Park,
    Library,
    Grocery,
    Healthcare,
    Dining,
    Shopping,
    Transit,
    Other,
}

public sealed class NeighborhoodInsight
{
    private NeighborhoodInsight()
    {
    }

    private NeighborhoodInsight(
        Guid organizationId,
        Guid propertyId,
        string providerPlaceId,
        NeighborhoodPlaceCategory category,
        string name,
        string address,
        decimal distanceMiles,
        string sourceUrl,
        bool hasPhoto,
        string? photoAttribution,
        string? photoAttributionUrl,
        string? photoSourceUrl,
        DateTimeOffset checkedAtUtc)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        ProviderPlaceId = Required(providerPlaceId, 300, nameof(providerPlaceId));
        Category = category;
        Name = Required(name, 300, nameof(name));
        Address = Required(address, 500, nameof(address));
        SourceUrl = Required(sourceUrl, 2_000, nameof(sourceUrl));
        HasPhoto = hasPhoto;
        PhotoAttribution = Optional(photoAttribution, 500, nameof(photoAttribution));
        PhotoAttributionUrl = Optional(photoAttributionUrl, 2_000, nameof(photoAttributionUrl));
        PhotoSourceUrl = Optional(photoSourceUrl, 2_000, nameof(photoSourceUrl));
        CheckedAtUtc = checkedAtUtc;
        SetDistance(distanceMiles);
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string ProviderPlaceId { get; private set; } = string.Empty;
    public NeighborhoodPlaceCategory Category { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public decimal DistanceMiles { get; private set; }
    public string SourceUrl { get; private set; } = string.Empty;
    public bool HasPhoto { get; private set; }
    public string? PhotoAttribution { get; private set; }
    public string? PhotoAttributionUrl { get; private set; }
    public string? PhotoSourceUrl { get; private set; }
    public string? VideoPhotoBlobPath { get; private set; }
    public string? VideoPhotoFilename { get; private set; }
    public string? VideoPhotoMimeType { get; private set; }
    public long? VideoPhotoFileSize { get; private set; }
    public int? VideoPhotoWidth { get; private set; }
    public int? VideoPhotoHeight { get; private set; }
    public string? VideoPhotoCredit { get; private set; }
    public DateTimeOffset? VideoPhotoUploadedAtUtc { get; private set; }
    public bool IsApproved { get; private set; }
    public DateTimeOffset CheckedAtUtc { get; private set; }
    public ListingProperty Property { get; private set; } = null!;

    public static NeighborhoodInsight Create(
        Guid organizationId,
        Guid propertyId,
        string providerPlaceId,
        NeighborhoodPlaceCategory category,
        string name,
        string address,
        decimal distanceMiles,
        string sourceUrl,
        bool hasPhoto,
        string? photoAttribution,
        string? photoAttributionUrl,
        string? photoSourceUrl,
        DateTimeOffset checkedAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property are required.");
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new NeighborhoodInsight(
            organizationId, propertyId, providerPlaceId, category, name, address, distanceMiles,
            sourceUrl, hasPhoto, photoAttribution, photoAttributionUrl, photoSourceUrl, checkedAtUtc);
    }

    public void SetApproval(bool approved) => IsApproved = approved;

    public void SetVideoPhoto(
        string blobPath,
        string filename,
        string mimeType,
        long fileSize,
        int width,
        int height,
        string credit,
        DateTimeOffset uploadedAtUtc)
    {
        if (fileSize <= 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSize), "Video-photo dimensions and size must be positive.");
        }

        VideoPhotoBlobPath = Required(blobPath, 1_000, nameof(blobPath));
        VideoPhotoFilename = Required(filename, 255, nameof(filename));
        VideoPhotoMimeType = Required(mimeType, 100, nameof(mimeType));
        VideoPhotoFileSize = fileSize;
        VideoPhotoWidth = width;
        VideoPhotoHeight = height;
        VideoPhotoCredit = Required(credit, 300, nameof(credit));
        VideoPhotoUploadedAtUtc = uploadedAtUtc;
    }

    public void CopyVideoPhotoFrom(NeighborhoodInsight source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.VideoPhotoBlobPath is null)
        {
            return;
        }

        SetVideoPhoto(
            source.VideoPhotoBlobPath,
            source.VideoPhotoFilename!,
            source.VideoPhotoMimeType!,
            source.VideoPhotoFileSize!.Value,
            source.VideoPhotoWidth!.Value,
            source.VideoPhotoHeight!.Value,
            source.VideoPhotoCredit!,
            source.VideoPhotoUploadedAtUtc!.Value);
    }

    public string? RemoveVideoPhoto()
    {
        var blobPath = VideoPhotoBlobPath;
        VideoPhotoBlobPath = null;
        VideoPhotoFilename = null;
        VideoPhotoMimeType = null;
        VideoPhotoFileSize = null;
        VideoPhotoWidth = null;
        VideoPhotoHeight = null;
        VideoPhotoCredit = null;
        VideoPhotoUploadedAtUtc = null;
        return blobPath;
    }

    private void SetDistance(decimal distanceMiles)
    {
        if (distanceMiles is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceMiles));
        }

        DistanceMiles = decimal.Round(distanceMiles, 2, MidpointRounding.AwayFromZero);
    }

    private static string Required(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maxLength} characters.");
    }

    private static string? Optional(string? value, int maxLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, maxLength, parameterName);
}
