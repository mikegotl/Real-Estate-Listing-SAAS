using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Web.Components.Properties;

public static class PropertyDisplay
{
    public static string Name(Enum value) => value switch
    {
        PropertyType.SingleFamily => "Single family",
        PropertyType.MultiFamily => "Multi-family",
        ListingStatus.ComingSoon => "Coming soon",
        ListingStatus.OffMarket => "Off market",
        _ => value.ToString(),
    };

    public static string Status(PropertySummary property) =>
        property.IsArchived ? "Archived" : Name(property.ListingStatus);

    // CSS modifier for the pipeline pill, matching the tone of the property's next step.
    public static string StageTone(PropertySummary property) => property switch
    {
        { IsArchived: true } => "muted",
        { LatestCampaignStatus: CampaignGenerationStatus.Failed } => "error",
        { LatestCampaignStatus: CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running } => "progress",
        { LatestCampaignStatus: CampaignGenerationStatus.Completed } => "success",
        _ => "attention",
    };

    public static string DetailsUrl(Guid propertyId, PropertyDetailsTab? tab = null) => tab is null
        ? $"/properties/{propertyId}"
        : $"/properties/{propertyId}?tab={tab.Value.ToString().ToLowerInvariant()}";
}
