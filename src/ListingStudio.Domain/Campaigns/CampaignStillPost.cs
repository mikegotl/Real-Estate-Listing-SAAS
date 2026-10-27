namespace ListingStudio.Domain.Campaigns;

public enum CampaignStillPostKind
{
    FacebookSquare,
    FacebookPortrait,
}

public enum CampaignStillPostStatus
{
    Planned,
    Rendered,
}

public sealed class CampaignStillPost
{
    private CampaignStillPost()
    {
    }

    private CampaignStillPost(
        Guid campaignGenerationJobId,
        Guid organizationId,
        Guid propertyId,
        CampaignStillPostKind kind,
        int width,
        int height,
        string caption,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        CampaignGenerationJobId = campaignGenerationJobId;
        OrganizationId = organizationId;
        PropertyId = propertyId;
        Kind = kind;
        Width = width;
        Height = height;
        Caption = Required(caption, 4_000, nameof(caption));
        Status = CampaignStillPostStatus.Planned;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public CampaignStillPostKind Kind { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string Caption { get; private set; } = string.Empty;

    public CampaignStillPostStatus Status { get; private set; }

    public string? AssetPath { get; private set; }

    public string? ContentType { get; private set; }

    public long? FileSize { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? RenderedAtUtc { get; private set; }

    public CampaignGenerationJob CampaignGenerationJob { get; private set; } = null!;

    public static CampaignStillPost Create(
        Guid campaignGenerationJobId,
        Guid organizationId,
        Guid propertyId,
        CampaignStillPostKind kind,
        int width,
        int height,
        string caption,
        DateTimeOffset now)
    {
        if (campaignGenerationJobId == Guid.Empty || organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Campaign, organization, and property identities are required.");
        }

        if (!Enum.IsDefined(kind) || width is < 320 or > 4_096 || height is < 320 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new CampaignStillPost(
            campaignGenerationJobId,
            organizationId,
            propertyId,
            kind,
            width,
            height,
            caption,
            now);
    }

    public void MarkRendered(string assetPath, string contentType, long fileSize, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSize);
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize;
        Status = CampaignStillPostStatus.Rendered;
        RenderedAtUtc = now;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }
}
