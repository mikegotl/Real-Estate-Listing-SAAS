using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Web.Components.Properties;

namespace ListingStudio.Web.Components.Workspace;

public sealed record WorkspaceAttentionItem(PropertySummary Property, PropertyNextStep NextStep);

public sealed record WorkspaceSummary(
    int ActiveListings,
    int ListingsNeedingPhotos,
    int CampaignsInProgress,
    int CampaignsReady,
    IReadOnlyList<WorkspaceAttentionItem> NeedsAttention,
    IReadOnlyList<PropertySummary> RecentlyUpdated)
{
    public const int MaximumAttentionItems = 5;
    public const int MaximumRecentItems = 3;

    public static WorkspaceSummary From(IReadOnlyList<PropertySummary> properties)
    {
        var active = properties.Where(property => !property.IsArchived).ToList();

        // Running and finished campaigns need no action, so only listings the user can move forward are surfaced.
        var attention = active
            .Select(property => (Property: property, Priority: AttentionPriority(property)))
            .Where(candidate => candidate.Priority is not null)
            .OrderBy(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Property.UpdatedAtUtc)
            .Take(MaximumAttentionItems)
            .Select(candidate => new WorkspaceAttentionItem(candidate.Property, NextStepFor(candidate.Property)))
            .ToList();

        return new WorkspaceSummary(
            active.Count,
            active.Count(property => property.PhotoCount == 0),
            active.Count(property => property.LatestCampaignStatus is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running),
            active.Count(property => property.LatestCampaignStatus == CampaignGenerationStatus.Completed),
            attention,
            [.. active.OrderByDescending(property => property.UpdatedAtUtc).Take(MaximumRecentItems)]);
    }

    public static PropertyNextStep NextStepFor(PropertySummary property) => PropertyNextSteps.Determine(
        property.IsArchived,
        property.PhotoCount,
        property.FailedAnalysisCount,
        property.LatestCampaignStatus);

    private static int? AttentionPriority(PropertySummary property) => property switch
    {
        { LatestCampaignStatus: CampaignGenerationStatus.Failed } => 0,
        { LatestCampaignStatus: CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running, FailedAnalysisCount: > 0 } => 1,
        { LatestCampaignStatus: CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running or CampaignGenerationStatus.Completed } => null,
        { PhotoCount: 0 } => 2,
        { PhotoCount: < PropertyNextSteps.RecommendedPhotoCount } => 3,
        _ => 4,
    };
}
