using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignLocalizationService
{
    Task<CampaignLocalizedCopy> LocalizeAsync(
        CampaignLocalizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CampaignLocalizationSegment(string Id, string Text);

public sealed record CampaignLocalizationRequest(
    CampaignNarrationPreferences Preferences,
    IReadOnlyList<CampaignLocalizationSegment> NarrationSegments);

public sealed record CampaignLocalizedSegment(string Id, string Text);

public sealed record CampaignLocalizedCopy(
    IReadOnlyList<CampaignLocalizedSegment> SpokenSegments,
    IReadOnlyList<CampaignLocalizedSegment> CaptionSegments);
