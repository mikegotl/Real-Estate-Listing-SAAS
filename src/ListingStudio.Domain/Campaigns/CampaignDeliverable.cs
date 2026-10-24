using ListingStudio.Domain.Videos;

namespace ListingStudio.Domain.Campaigns;

public enum CampaignOutputKind
{
    Hero,
    Feature,
    Teaser,
    LongForm,
    TikTok,
    InstagramReel,
    YouTubeShort,
    YouTubeTour,
    FullNarratedTour,
    FacebookFeed,
}

[Flags]
public enum CampaignOutputSelection
{
    None = 0,
    LegacyStandard = 1 << 0,
    TikTok = 1 << 1,
    InstagramReel = 1 << 2,
    YouTubeShort = 1 << 3,
    YouTubeTour = 1 << 4,
    FullNarratedTour = 1 << 5,
    FacebookFeed = 1 << 6,
}

public static class CampaignOutputSelectionPolicy
{
    /// <summary>
    /// TikTok, Reels and Shorts share one vertical edit, so new campaigns render it once as the TikTok output. The
    /// Instagram Reel and YouTube Short selections remain for campaigns created before that.
    /// </summary>
    public const CampaignOutputSelection Recommended = CampaignOutputSelection.TikTok
        | CampaignOutputSelection.YouTubeTour
        | CampaignOutputSelection.FacebookFeed;

    public const CampaignOutputSelection All = CampaignOutputSelection.LegacyStandard
        | Recommended
        | CampaignOutputSelection.InstagramReel
        | CampaignOutputSelection.YouTubeShort
        | CampaignOutputSelection.FullNarratedTour;

    public static void Validate(CampaignOutputSelection selection)
    {
        if (selection == CampaignOutputSelection.None || (selection & ~All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "Choose at least one supported campaign output.");
        }

        if (selection.HasFlag(CampaignOutputSelection.LegacyStandard)
            && selection != CampaignOutputSelection.LegacyStandard)
        {
            throw new ArgumentException("Legacy campaign outputs cannot be combined with platform outputs.", nameof(selection));
        }
    }
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
        Guid videoNarrationId,
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
        VideoNarrationId = videoNarrationId;
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

    public Guid VideoNarrationId { get; private set; }

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

    public VideoNarration VideoNarration { get; private set; } = null!;

    public static CampaignDeliverable Create(
        Guid campaignGenerationJobId,
        Guid organizationId,
        Guid propertyId,
        Guid videoNarrationId,
        CampaignOutputKind kind,
        RequestedDuration requestedDuration,
        VideoAspectRatio aspectRatio,
        string specificationJson,
        DateTimeOffset now)
    {
        if (campaignGenerationJobId == Guid.Empty || organizationId == Guid.Empty || propertyId == Guid.Empty
            || videoNarrationId == Guid.Empty)
        {
            throw new ArgumentException("Campaign, organization, and property identities are required.");
        }

        if (!Enum.IsDefined(kind) || (int)requestedDuration is < 15 or > 900 || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new CampaignDeliverable(
            campaignGenerationJobId,
            organizationId,
            propertyId,
            videoNarrationId,
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

    public void UpdateSpecification(string specificationJson)
    {
        if (Status != CampaignDeliverableStatus.Planned)
        {
            throw new InvalidOperationException("A rendered campaign specification cannot be changed.");
        }

        SpecificationJson = Required(specificationJson, 500_000, nameof(specificationJson));
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
