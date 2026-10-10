using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignGenerationTimeoutTests
{
    [Theory]
    [InlineData(CampaignGenerationStage.RenderHero, 60, 600)]
    [InlineData(CampaignGenerationStage.RenderLongForm, null, 600)]
    [InlineData(CampaignGenerationStage.RenderLongForm, 736, 2_328)]
    [InlineData(CampaignGenerationStage.RenderLongForm, 900, 2_820)]
    public void LongFormStageTimeoutScalesWithProgramLength(
        CampaignGenerationStage stage,
        int? duration,
        int expected)
    {
        Assert.Equal(
            expected,
            CampaignGenerationProcessor.CalculateStageTimeoutSeconds(600, stage, duration));
    }
}
