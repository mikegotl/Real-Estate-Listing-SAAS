using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface ICampaignDerivativeGenerator
{
    CampaignDerivativeSet Generate(CampaignDerivativeRequest request);
}

public sealed record CampaignDerivativeMedia(Guid PropertyMediaId, int Width, int Height);

public sealed record CampaignDerivativeVideo(Guid PropertyVideoId, int Width, int Height, int DurationMs);

public sealed record CampaignDerivativeRequest(
    VideoProductionSpecification MasterSpecification,
    IReadOnlyList<CampaignDerivativeMedia> PropertyMedia,
    IReadOnlyList<CampaignDerivativeVideo>? PropertyVideos = null);

public enum CampaignDeliverableKind
{
    Hero,
    Feature,
    Teaser,
}

public sealed record CampaignDerivative(
    CampaignDeliverableKind Kind,
    VideoAspectRatio AspectRatio,
    VideoProductionSpecification Specification,
    IReadOnlySet<Guid> ReusedPropertyMediaIds,
    IReadOnlySet<Guid> ReusedGeneratedClipIds,
    IReadOnlySet<Guid>? ReusedPropertyVideoIds = null);

public sealed record CampaignDerivativeSet(IReadOnlyList<CampaignDerivative> Derivatives)
{
    public CampaignDerivative Get(CampaignDeliverableKind kind, VideoAspectRatio aspectRatio) =>
        Derivatives.Single(item => item.Kind == kind && item.AspectRatio == aspectRatio);
}
