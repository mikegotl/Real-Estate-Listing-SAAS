namespace ListingStudio.Domain.Properties;

public sealed class PropertyNarrationScript
{
    private PropertyNarrationScript()
    {
    }

    private PropertyNarrationScript(
        Guid organizationId,
        Guid propertyId,
        string blobPath,
        string originalFilename,
        string contentType,
        long fileSize,
        string extractedText,
        DateTimeOffset uploadedAtUtc)
    {
        Id = Guid.NewGuid();
        OrganizationId = RequiredId(organizationId, nameof(organizationId));
        PropertyId = RequiredId(propertyId, nameof(propertyId));
        Replace(blobPath, originalFilename, contentType, fileSize, extractedText, uploadedAtUtc);
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string BlobPath { get; private set; } = string.Empty;
    public string OriginalFilename { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public string ExtractedText { get; private set; } = string.Empty;
    public DateTimeOffset UploadedAtUtc { get; private set; }
    public bool MarketingUseAccepted { get; private set; }
    public DateTimeOffset? MarketingUseAcceptedAtUtc { get; private set; }
    public string? MarketingUseAcceptedByUserId { get; private set; }
    public ListingProperty Property { get; private set; } = null!;

    public static PropertyNarrationScript Create(
        Guid organizationId,
        Guid propertyId,
        string blobPath,
        string originalFilename,
        string contentType,
        long fileSize,
        string extractedText,
        DateTimeOffset uploadedAtUtc) => new(
            organizationId,
            propertyId,
            blobPath,
            originalFilename,
            contentType,
            fileSize,
            extractedText,
            uploadedAtUtc);

    public void Replace(
        string blobPath,
        string originalFilename,
        string contentType,
        long fileSize,
        string extractedText,
        DateTimeOffset uploadedAtUtc)
    {
        BlobPath = Required(blobPath, 1_024, nameof(blobPath));
        OriginalFilename = Required(originalFilename, 255, nameof(originalFilename));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize > 0
            ? fileSize
            : throw new ArgumentOutOfRangeException(nameof(fileSize), "File size must be positive.");
        ExtractedText = Required(extractedText, 12_000, nameof(extractedText));
        UploadedAtUtc = uploadedAtUtc;
        RevokeMarketingUse();
    }

    public void SetMarketingUseAccepted(bool accepted, string userId, DateTimeOffset now)
    {
        if (!accepted)
        {
            RevokeMarketingUse();
            return;
        }

        MarketingUseAccepted = true;
        MarketingUseAcceptedAtUtc = now;
        MarketingUseAcceptedByUserId = Required(userId, 450, nameof(userId));
    }

    private void RevokeMarketingUse()
    {
        MarketingUseAccepted = false;
        MarketingUseAcceptedAtUtc = null;
        MarketingUseAcceptedByUserId = null;
    }

    private static Guid RequiredId(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("An identity is required.", name) : value;

    private static string Required(string value, int maximumLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(name, $"Value cannot exceed {maximumLength} characters.");
    }
}
