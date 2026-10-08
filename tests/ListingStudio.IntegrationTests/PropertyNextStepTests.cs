using ListingStudio.Domain.Campaigns;
using ListingStudio.Web.Components.Properties;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyNextStepTests
{
    [Fact]
    public void ArchivedPropertyHasNoAction()
    {
        var step = PropertyNextSteps.Determine(isArchived: true, photoCount: 0, failedAnalysisCount: 0, campaignStatus: null);

        Assert.Null(step.Tab);
        Assert.Null(step.ActionLabel);
    }

    [Theory]
    [InlineData(0, 0, null, PropertyDetailsTab.Media, "Add listing photos")]
    [InlineData(2, 0, null, PropertyDetailsTab.Media, "Add a few more photos")]
    [InlineData(3, 0, null, PropertyDetailsTab.Campaign, "Generate your campaign")]
    [InlineData(3, 0, CampaignGenerationStatus.Running, PropertyDetailsTab.Campaign, "Campaign in progress")]
    [InlineData(3, 1, CampaignGenerationStatus.Queued, PropertyDetailsTab.Media, "Campaign waiting on photo analysis")]
    [InlineData(3, 0, CampaignGenerationStatus.Failed, PropertyDetailsTab.Campaign, "Campaign needs attention")]
    [InlineData(3, 0, CampaignGenerationStatus.Completed, PropertyDetailsTab.Campaign, "Campaign ready")]
    [InlineData(1, 0, CampaignGenerationStatus.Completed, PropertyDetailsTab.Campaign, "Campaign ready")]
    [InlineData(2, 0, CampaignGenerationStatus.Cancelled, PropertyDetailsTab.Media, "Add a few more photos")]
    public void NextStepFollowsListingProgress(
        int photoCount,
        int failedAnalysisCount,
        CampaignGenerationStatus? campaignStatus,
        PropertyDetailsTab expectedTab,
        string expectedTitle)
    {
        var step = PropertyNextSteps.Determine(false, photoCount, failedAnalysisCount, campaignStatus);

        Assert.Equal(expectedTab, step.Tab);
        Assert.Equal(expectedTitle, step.Title);
        Assert.NotNull(step.ActionLabel);
    }
}
