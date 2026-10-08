using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Web.Components.Properties;

public enum PropertyDetailsTab
{
    Overview = 0,
    Media = 1,
    Neighborhood = 2,
    Story = 3,
    Campaign = 4,
}

public sealed record PropertyNextStep(string Title, string Description, string? ActionLabel, PropertyDetailsTab? Tab);

public static class PropertyNextSteps
{
    public const int RecommendedPhotoCount = 3;

    public static PropertyNextStep Determine(
        bool isArchived,
        int photoCount,
        int failedAnalysisCount,
        CampaignGenerationStatus? campaignStatus)
    {
        if (isArchived)
        {
            return new("Archived listing", "This property is read-only. Its media and campaign assets remain available for review.", null, null);
        }

        if (photoCount == 0)
        {
            return new("Add listing photos", "Upload interior and exterior photos. They power the story, the video plan and every campaign deliverable.", "Add photos", PropertyDetailsTab.Media);
        }

        if (campaignStatus is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running)
        {
            return failedAnalysisCount > 0
                ? new("Campaign waiting on photo analysis", "Some photos failed analysis and are being retried. Check them in Media.", "Review photos", PropertyDetailsTab.Media)
                : new("Campaign in progress", "Listing Studio is producing your videos and social copy. You can leave this page; progress is saved.", "View progress", PropertyDetailsTab.Campaign);
        }

        if (campaignStatus == CampaignGenerationStatus.Failed)
        {
            return new("Campaign needs attention", "A campaign stage failed. Review the error and retry from the failed stage.", "Open campaign", PropertyDetailsTab.Campaign);
        }

        if (campaignStatus == CampaignGenerationStatus.Completed)
        {
            return new("Campaign ready", "Preview the HERO, FEATURE and TEASER videos, then download them and copy the social captions.", "View deliverables", PropertyDetailsTab.Campaign);
        }

        if (photoCount < RecommendedPhotoCount)
        {
            return new(
                "Add a few more photos",
                $"You have {photoCount} {(photoCount == 1 ? "photo" : "photos")}. At least {RecommendedPhotoCount} interior and exterior photos make a more varied video.",
                "Add photos",
                PropertyDetailsTab.Media);
        }

        return new("Generate your campaign", "Your listing has enough media. Generate the story, narration and three videos in one step.", "Go to campaign", PropertyDetailsTab.Campaign);
    }
}
