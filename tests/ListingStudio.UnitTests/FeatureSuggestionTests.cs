using ListingStudio.Domain.Feedback;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class FeatureSuggestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateTrimsCommentAndLinksRequiredCampaign()
    {
        var campaignId = Guid.NewGuid();

        var suggestion = FeatureSuggestion.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "agent-id",
            "  Add a floor-plan overlay.  ",
            requiredForActiveCampaign: true,
            campaignId,
            Now);

        Assert.Equal("Add a floor-plan overlay.", suggestion.Comment);
        Assert.True(suggestion.RequiredForActiveCampaign);
        Assert.Equal(campaignId, suggestion.CampaignGenerationJobId);
    }

    [Fact]
    public void CreateRejectsMissingCommentAndInconsistentCampaignLink()
    {
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", " ", false, null, Now));
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", "Needed now", true, null, Now));
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", "General idea", false, Guid.NewGuid(), Now));
    }
}
