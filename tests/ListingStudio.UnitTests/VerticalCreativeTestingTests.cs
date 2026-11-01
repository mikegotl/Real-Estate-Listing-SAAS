using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>Hook B wording, creative test case planning and the suite's sequential case rules.</summary>
public sealed class VerticalCreativeTestingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static VerticalCreativePlan RichPlan() => VerticalCreativePlan.Create(RecipeFixture.Rich() with
    {
        AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private showings this weekend only"),
    });

    private static VerticalCreativeTestPreview Preview(VerticalCreativePlan? plan = null) =>
        VerticalCreativeTestPlanner.Preview(Guid.NewGuid(), "1 Main St, Raleigh, NC", plan ?? RichPlan());

    [Fact]
    public void HookBRewordsTheSameGroundedFactAndStaysValid()
    {
        var plan = RichPlan();
        var price = plan.Recipes.Single(recipe => recipe.Format == VerticalCreativeFormat.PriceFirst);
        var priceB = VerticalHookVariants.ApplyB(price)!;
        Assert.Equal("What $450,000 gets you in Raleigh, NC", priceB.Hook.Text);
        Assert.Empty(priceB.Validate());
        var fact = priceB.Facts.Single(item => item.Key == priceB.Hook.GroundingKey);
        Assert.Equal(VerticalCopySource.VerifiedProperty, fact.Source);
        Assert.DoesNotContain(priceB.Beats, beat => beat.Overlay == price.Hook);
        Assert.Contains(priceB.Beats, beat => beat.Overlay == priceB.Hook);
        Assert.Equal(price.Beats.Count, priceB.Beats.Count);
        Assert.Equal(price.Facts.Count + 1, priceB.Facts.Count);

        var wow = plan.Recipes.Single(recipe => recipe.Format == VerticalCreativeFormat.WowFeature);
        var wowB = VerticalHookVariants.ApplyB(wow)!;
        Assert.StartsWith("Look at this ", wow.Hook.Text, StringComparison.Ordinal);
        Assert.Equal("Wait until you see this " + wow.Hook.Text["Look at this ".Length..], wowB.Hook.Text);
        Assert.Equal(VerticalCaptions(wowB), VerticalVariantCaptions.From(wowB).Hook);
    }

    [Fact]
    public void ExclusiveAccessHasNoHookBBecauseItWouldBeANewAccessClaim()
    {
        var plan = RichPlan();
        var access = plan.Recipes.SingleOrDefault(recipe => recipe.Format == VerticalCreativeFormat.ExclusiveAccess);
        Assert.False(VerticalHookVariants.Supports(VerticalCreativeFormat.ExclusiveAccess));
        if (access is not null)
        {
            Assert.Null(VerticalHookVariants.ApplyB(access));
        }

        var preview = Preview(plan);
        var cases = VerticalCreativeTestPlanner.Plan(preview, VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.ExclusiveAccess);
        Assert.Equal(["a", "b"], cases.Select(item => item.HookVariant));
        Assert.NotNull(cases[1].SkipReason);
        Assert.Contains("hook B", cases[1].SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModesPlanTheRightCasesAndSkipFormatsTheListingCantSupport()
    {
        var basic = Preview(VerticalCreativePlan.Create(RecipeFixture.Input()));
        var recommended = VerticalCreativeTestPlanner.Plan(basic, VerticalCreativeTestMode.RecommendedThree, null);
        Assert.Equal(basic.Recommended.Select(option => option.Format), recommended.Select(item => item.Format));
        Assert.All(recommended, item => Assert.Null(item.SkipReason));

        var all = VerticalCreativeTestPlanner.Plan(basic, VerticalCreativeTestMode.AllEligible, null);
        Assert.Equal(VerticalFormatRules.Formats.Count, all.Count);
        var story = all.Single(item => item.Format == VerticalCreativeFormat.StoryReveal);
        Assert.StartsWith("This listing can't support Story reveal", story.SkipReason, StringComparison.Ordinal);
        Assert.Equal(basic.Eligible.Count, VerticalCreativeTestPlanner.GeneratedCount(all));

        var single = Assert.Single(VerticalCreativeTestPlanner.Plan(basic, VerticalCreativeTestMode.SingleFormat, VerticalCreativeFormat.PriceFirst));
        Assert.Equal(("a", "price-first-v1", 30), (single.HookVariant, single.RecipeVersion, single.ExpectedDurationSeconds));
        Assert.Contains("listing price", single.ReasonsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => VerticalCreativeTestPlanner.Plan(basic, VerticalCreativeTestMode.SingleFormat, null));
    }

    [Fact]
    public void CasesRunOneAtATimeAndAFailureNeverErasesAPassedCase()
    {
        var cases = VerticalCreativeTestPlanner.Plan(Preview(), VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.WowFeature);
        var suite = VerticalCreativeTestSuite.Create(Guid.NewGuid(), Guid.NewGuid(), "admin", VerticalCreativeTestMode.HookAb,
            VerticalCreativeFormat.WowFeature, "vertical-rules-v1", cases, Now);
        Assert.Equal(2, suite.Cases.Count);
        Assert.NotEqual(suite.Cases[0].CaseKey, suite.Cases[1].CaseKey);

        var first = suite.NextCase!;
        Assert.Equal(0, first.Index);
        first.Start(Guid.NewGuid(), Now);
        Assert.Null(suite.NextCase);
        first.Pass(Guid.NewGuid(), Guid.NewGuid(), Now);
        suite.CompleteIfDone(Now);
        Assert.Equal(VerticalCreativeTestSuiteStatus.Queued, suite.Status);

        var second = suite.NextCase!;
        second.Start(Guid.NewGuid(), Now);
        second.Fail("The file did not decode cleanly.", null, null, Now);
        suite.CompleteIfDone(Now);
        Assert.Equal(VerticalCreativeTestSuiteStatus.Completed, suite.Status);
        Assert.Equal(VerticalCreativeTestCaseStatus.Passed, first.Status);
        Assert.NotNull(first.CampaignDeliverableId);
    }

    [Fact]
    public void CancellingKeepsFinishedCasesAndAllSkippedSuitesCompleteAtOnce()
    {
        var preview = Preview();
        var suite = VerticalCreativeTestSuite.Create(Guid.NewGuid(), Guid.NewGuid(), "admin", VerticalCreativeTestMode.RecommendedThree,
            null, "vertical-rules-v1", VerticalCreativeTestPlanner.Plan(preview, VerticalCreativeTestMode.RecommendedThree, null), Now);
        var first = suite.NextCase!;
        first.Start(Guid.NewGuid(), Now);
        first.Pass(Guid.NewGuid(), Guid.NewGuid(), Now);
        suite.Cancel("Cancelled.", Now);
        Assert.Equal(VerticalCreativeTestSuiteStatus.Cancelled, suite.Status);
        Assert.Equal(VerticalCreativeTestCaseStatus.Passed, first.Status);
        Assert.All(suite.Cases.Skip(1), item => Assert.Equal(VerticalCreativeTestCaseStatus.Cancelled, item.Status));

        var skipped = VerticalCreativeTestSuite.Create(Guid.NewGuid(), Guid.NewGuid(), "admin", VerticalCreativeTestMode.SingleFormat,
            VerticalCreativeFormat.StoryReveal, "vertical-rules-v1",
            [new VerticalCreativeTestCasePlan(VerticalCreativeFormat.StoryReveal, "a", 0, "[]", "story-reveal-v1", 30, "Missing a story")], Now);
        Assert.Equal(VerticalCreativeTestSuiteStatus.Completed, skipped.Status);
        Assert.Equal(VerticalCreativeTestCaseStatus.Skipped, Assert.Single(skipped.Cases).Status);

        Assert.Throws<ArgumentException>(() => VerticalCreativeTestSuite.Create(Guid.NewGuid(), Guid.NewGuid(), "admin",
            VerticalCreativeTestMode.HookAb, null, "vertical-rules-v1", [], Now));
    }

    [Fact]
    public void OnlyOneChosenFormatCanAskForHookB()
    {
        Assert.Throws<ArgumentException>(() => RequestHook(VerticalVideoSelection.RecommendedThree, null, "b"));
        Assert.Throws<ArgumentException>(() => RequestHook(VerticalVideoSelection.AgentChosen,
            [VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst], "b"));
        var job = RequestHook(VerticalVideoSelection.AgentChosen, [VerticalCreativeFormat.WowFeature], "b");
        Assert.Equal("b", job.VerticalHookVariant);
        job.UseLegacyVertical();
        Assert.Null(job.VerticalHookVariant);
    }

    private static string VerticalCaptions(VerticalCreativeRecipe recipe) => recipe.Hook.Text;

    private static CampaignGenerationJob RequestHook(VerticalVideoSelection selection, IReadOnlyList<VerticalCreativeFormat>? formats, string hook)
    {
        var job = CampaignGenerationJob.Create(Guid.NewGuid(), Guid.NewGuid(), "agent", "fingerprint", CampaignOutputSelection.TikTok,
            Now, CampaignCreativeOptions.SocialDefault, CampaignNarrationPreferences.Default);
        job.RequestVerticalLibrary(selection, null, formats, hook);
        return job;
    }
}
