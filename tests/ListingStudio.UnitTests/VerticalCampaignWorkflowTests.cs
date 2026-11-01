using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VerticalCampaignWorkflowTests
{
    private static VerticalCreativePlan RichPlan() => VerticalCreativePlan.Create(RecipeFixture.Rich() with
    {
        AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private showings this weekend only"),
    });

    [Fact]
    public void PickerStartsWithTheRecommendedVideosAndSendsTheNamedSelection()
    {
        var plan = RichPlan();
        var picker = new VerticalVideoPicker(plan.Recommendation);

        Assert.Equal(3, picker.Recommended.Count);
        Assert.True(picker.Eligible.Count > picker.Recommended.Count);
        Assert.Equal(picker.Recommended, picker.Chosen);
        Assert.Equal(new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree, VerticalCampaignGoal.NewListing),
            picker.ToRequest(VerticalCampaignGoal.NewListing));

        picker.UseAllEligible();
        var all = picker.ToRequest(null);
        Assert.Equal(VerticalVideoSelection.AllEligible, all.Selection);
        Assert.Null(all.Formats);
        Assert.Equal(picker.Eligible, picker.Chosen);
    }

    [Fact]
    public void RemovingOrSwappingAVideoSendsTheAgentsOwnListInRankOrder()
    {
        var plan = RichPlan();
        var picker = new VerticalVideoPicker(plan.Recommendation);
        var first = picker.Recommended[0];
        var extra = picker.Eligible.First(format => !picker.Recommended.Contains(format));

        Assert.True(picker.Set(first, false));
        var removed = picker.ToRequest(VerticalCampaignGoal.MaximizeReach);
        Assert.Equal(VerticalVideoSelection.AgentChosen, removed.Selection);
        Assert.Equal(picker.Recommended.Skip(1), removed.Formats);
        Assert.Equal(VerticalCampaignGoal.MaximizeReach, removed.Goal);
        removed.Validate();

        picker.UseRecommended();
        Assert.True(picker.Substitute(first, extra));
        Assert.DoesNotContain(first, picker.Chosen);
        Assert.Contains(extra, picker.Chosen);
        Assert.Equal(picker.Chosen.OrderBy(format => plan.Recommendation.For(format).Rank), picker.Chosen);
        Assert.False(picker.Substitute(first, extra));

        picker.Set(extra, false);
        picker.Set(picker.Chosen[0], false);
        picker.Set(picker.Chosen[0], false);
        Assert.Empty(picker.Chosen);
        Assert.Throws<InvalidOperationException>(() => picker.ToRequest(null));
    }

    [Fact]
    public void IneligibleFormatsCanNeverBePickedOrRestored()
    {
        var plan = VerticalCreativePlan.Create(RecipeFixture.Input());
        var picker = new VerticalVideoPicker(plan.Recommendation);
        var ineligible = plan.Recommendation.Formats.Where(format => !format.IsEligible).Select(format => format.Format).ToArray();
        Assert.Contains(VerticalCreativeFormat.StoryReveal, ineligible);

        Assert.False(picker.Set(VerticalCreativeFormat.StoryReveal, true));
        Assert.False(picker.Substitute(picker.Chosen[0], VerticalCreativeFormat.StoryReveal));
        picker.Restore(VerticalVideoSelection.AgentChosen, [VerticalCreativeFormat.StoryReveal, picker.Eligible[0]]);
        Assert.Equal([picker.Eligible[0]], picker.Chosen);

        var selected = plan.Recommendation.Select(
            VerticalVideoSelection.AgentChosen,
            [VerticalCreativeFormat.StoryReveal, picker.Eligible[0]]);
        Assert.Equal([picker.Eligible[0]], selected.Select(item => item.Format));
    }

    [Fact]
    public void RequestShapeIsValidatedBeforeAnythingIsQueued()
    {
        new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree).Validate();
        new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen, null, [VerticalCreativeFormat.PriceFirst]).Validate();

        Assert.Throws<ArgumentException>(() => new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen).Validate());
        Assert.Throws<ArgumentException>(() => new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen, null,
            [VerticalCreativeFormat.PriceFirst, VerticalCreativeFormat.PriceFirst]).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen, null,
            [VerticalCreativeFormat.LegacySingle]).Validate());
        Assert.Throws<ArgumentException>(() => new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree, null,
            [VerticalCreativeFormat.PriceFirst]).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree, (VerticalCampaignGoal)99).Validate());
    }

    [Fact]
    public void CampaignKeepsTheGoalAndTheAgentsFormatsUntilItFallsBack()
    {
        var job = CampaignGenerationJob.Create(
            Guid.NewGuid(), Guid.NewGuid(), "user", "fingerprint", CampaignOutputSelection.TikTok, DateTimeOffset.UtcNow);

        job.RequestVerticalLibrary(
            VerticalVideoSelection.AgentChosen,
            VerticalCampaignGoal.OpenHouse,
            [VerticalCreativeFormat.HostedPov, VerticalCreativeFormat.WowFeature]);

        Assert.True(job.UsesVerticalLibrary);
        Assert.Equal(VerticalCampaignGoal.OpenHouse, job.VerticalGoal);
        Assert.Equal("HostedPov,WowFeature", job.VerticalFormatsValue);
        Assert.Equal([VerticalCreativeFormat.HostedPov, VerticalCreativeFormat.WowFeature], job.VerticalFormats);

        job.UseLegacyVertical();
        Assert.False(job.UsesVerticalLibrary);
        Assert.Empty(job.VerticalFormats);
        Assert.Equal(VerticalCampaignGoal.OpenHouse, job.VerticalGoal);

        var named = CampaignGenerationJob.Create(
            Guid.NewGuid(), Guid.NewGuid(), "user", "fingerprint", CampaignOutputSelection.TikTok, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => named.RequestVerticalLibrary(
            VerticalVideoSelection.RecommendedThree, null, [VerticalCreativeFormat.WowFeature]));
        Assert.Throws<ArgumentException>(() => named.RequestVerticalLibrary(VerticalVideoSelection.AgentChosen));
    }

    [Fact]
    public void GoalsStartFromACampaignMessageButNeverPickAFormat()
    {
        var templates = Enum.GetValues<VerticalCampaignGoal>().Select(VerticalFormatNames.TemplateFor).ToArray();

        Assert.Equal(templates.Length, templates.Distinct().Count());
        Assert.Equal(CampaignMarketingTemplate.OpenHouse, VerticalFormatNames.TemplateFor(VerticalCampaignGoal.OpenHouse));
        Assert.Equal(CampaignMarketingTemplate.PriceDrop, VerticalFormatNames.TemplateFor(VerticalCampaignGoal.PriceChange));
        Assert.Equal(CampaignMarketingTemplate.JustListed, VerticalFormatNames.TemplateFor(VerticalCampaignGoal.NewListing));
        Assert.All(Enum.GetValues<VerticalCampaignGoal>(), goal =>
        {
            Assert.False(string.IsNullOrWhiteSpace(VerticalFormatNames.Goal(goal)));
            Assert.False(string.IsNullOrWhiteSpace(VerticalFormatNames.GoalDescription(goal)));
        });
        Assert.All(VerticalFormatRules.Formats, format =>
            Assert.False(string.IsNullOrWhiteSpace(VerticalFormatNames.Description(format))));
    }
}
