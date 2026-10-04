using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface ICampaignDerivativeGenerator
{
    CampaignDerivativeSet Generate(CampaignDerivativeRequest request);
}

public sealed record CampaignDerivativeMedia(Guid PropertyMediaId, int Width, int Height);

public sealed record CampaignDerivativeRequest(
    VideoProductionSpecification MasterSpecification,
    IReadOnlyList<CampaignDerivativeMedia> PropertyMedia);

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
    IReadOnlySet<Guid> ReusedGeneratedClipIds);

public sealed record CampaignDerivativeSet(IReadOnlyList<CampaignDerivative> Derivatives)
{
    public CampaignDerivative Get(CampaignDeliverableKind kind, VideoAspectRatio aspectRatio) =>
        Derivatives.Single(item => item.Kind == kind && item.AspectRatio == aspectRatio);
}
