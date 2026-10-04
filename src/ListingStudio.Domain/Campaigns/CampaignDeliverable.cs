using ListingStudio.Domain.Videos;

namespace ListingStudio.Domain.Campaigns;

public enum CampaignOutputKind
{
    Hero,
    Feature,
    Teaser,
}

public enum CampaignDeliverableStatus
{
    Planned,
    Rendered,
}

public sealed class CampaignDeliverable
{
    private CampaignDeliverable()
    {
    }

    private CampaignDeliverable(
        Guid campaignGenerationJobId,
        Guid organizationId,
        Guid propertyId,
        CampaignOutputKind kind,
        RequestedDuration requestedDuration,
        VideoAspectRatio aspectRatio,
        string specificationJson,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        CampaignGenerationJobId = campaignGenerationJobId;
        OrganizationId = organizationId;
        PropertyId = propertyId;
        Kind = kind;
        RequestedDuration = requestedDuration;
        AspectRatio = aspectRatio;
        SpecificationJson = Required(specificationJson, 500_000, nameof(specificationJson));
        Status = CampaignDeliverableStatus.Planned;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public CampaignOutputKind Kind { get; private set; }

    public RequestedDuration RequestedDuration { get; private set; }

    public VideoAspectRatio AspectRatio { get; private set; }

    public string SpecificationJson { get; private set; } = string.Empty;

    public CampaignDeliverableStatus Status { get; private set; }

    public string? AssetPath { get; private set; }

    public string? ContentType { get; private set; }

    public long? FileSize { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? RenderedAtUtc { get; private set; }

    public CampaignGenerationJob CampaignGenerationJob { get; private set; } = null!;

    public static CampaignDeliverable Create(
        Guid campaignGenerationJobId,
        Guid organizationId,
        Guid propertyId,
        CampaignOutputKind kind,
        RequestedDuration requestedDuration,
        VideoAspectRatio aspectRatio,
        string specificationJson,
        DateTimeOffset now)
    {
        if (campaignGenerationJobId == Guid.Empty || organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Campaign, organization, and property identities are required.");
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(requestedDuration) || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new CampaignDeliverable(
            campaignGenerationJobId,
            organizationId,
            propertyId,
            kind,
            requestedDuration,
            aspectRatio,
            specificationJson,
            now);
    }

    public void MarkRendered(string assetPath, string contentType, long fileSize, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSize);
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize;
        Status = CampaignDeliverableStatus.Rendered;
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
