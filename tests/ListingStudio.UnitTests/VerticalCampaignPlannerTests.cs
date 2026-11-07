using Bunit;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using ListingStudio.Web.Components.Properties;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>Renders the recommendation-led vertical workflow with real recommendation data.</summary>
public sealed class VerticalCampaignPlannerTests : IAsyncDisposable
{
    private readonly BunitContext context = new();

    public VerticalCampaignPlannerTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    private static VerticalCreativePlan RichPlan() => VerticalCreativePlan.Create(RecipeFixture.Rich() with
    {
        AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private showings this weekend only"),
    });

    private IRenderedComponent<VerticalCampaignPlanner> Render(
        VerticalCreativePlan? plan,
        VerticalVideoPicker? picker,
        Action<VerticalCampaignGoal>? goalChanged = null,
        int otherOutputs = 0) =>
        context.Render<VerticalCampaignPlanner>(parameters => parameters
            .Add(component => component.Plan, plan)
            .Add(component => component.Picker, picker)
            .Add(component => component.OtherOutputCount, otherOutputs)
            .Add(component => component.GoalChanged, goal => goalChanged?.Invoke(goal)));

    [Fact]
    public void ShowsEveryFormatWithItsScoreReasonsHookLengthAndOpeningShot()
    {
        var plan = RichPlan();
        var picker = new VerticalVideoPicker(plan.Recommendation);

        var cut = Render(plan, picker, otherOutputs: 2);
        Assert.Equal("/go/article/one-listing-four-tiktoks?from=recommendations",
            cut.Find("[data-testid=guide-link-recommendations]").GetAttribute("href"));

        foreach (var item in plan.Recommendation.Formats)
        {
            var card = cut.Find($"[data-testid=format-card-{item.Format}]");
            Assert.Contains(VerticalFormatNames.Name(item.Format), card.TextContent, StringComparison.Ordinal);
            Assert.Contains(VerticalFormatNames.Purpose(item.Purpose), card.TextContent, StringComparison.Ordinal);
            Assert.Contains($"{item.Score}/100", card.TextContent, StringComparison.Ordinal);
            Assert.Contains(VerticalFormatNames.Strength(item.Strength), card.TextContent, StringComparison.Ordinal);
            Assert.All(item.Reasons, reason => Assert.Contains(reason, card.TextContent, StringComparison.Ordinal));
            Assert.Equal(item.IsRecommended, card.TextContent.Contains("Recommended", StringComparison.Ordinal));
            var recipe = plan.Recipes.Single(candidate => candidate.Format == item.Format);
            Assert.Contains(recipe.Hook.Text, cut.Find($"[data-testid=hook-{item.Format}]").TextContent, StringComparison.Ordinal);
            Assert.Contains($"{recipe.DurationSeconds} seconds", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("Opens on:", card.TextContent, StringComparison.Ordinal);
        }

        Assert.Equal(VerticalCampaignGoals.Offered.Count, cut.FindAll("[data-testid^=goal-]").Count);
        Assert.All(VerticalCampaignGoals.Offered, goal =>
            Assert.Contains("tiktok.com", cut.Find($"[data-testid=evidence-{goal}] a").GetAttribute("href"), StringComparison.Ordinal));
        Assert.Equal(VerticalCampaignGoals.Announcements.Count, cut.FindAll("[data-testid^=announcement-]").Count);
        Assert.Contains("You'll get 3 vertical videos and 2 other files.", cut.Find("[data-testid=vertical-estimate]").TextContent,
            StringComparison.Ordinal);
        Assert.Contains("one campaign against your monthly allowance", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("TikTok, Instagram Reels or YouTube Shorts", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("open with the price and location", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("One file to post on all three", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void IneligibleFormatsExplainWhatIsMissingAndCannotBePicked()
    {
        var plan = VerticalCreativePlan.Create(RecipeFixture.Input());
        var picker = new VerticalVideoPicker(plan.Recommendation);

        var cut = Render(plan, picker);

        var story = plan.Recommendation.For(VerticalCreativeFormat.StoryReveal);
        Assert.False(story.IsEligible);
        var missing = cut.Find("[data-testid=missing-StoryReveal]").TextContent;
        Assert.All(story.MissingInputs, input => Assert.Contains(input, missing, StringComparison.Ordinal));
        Assert.Contains("Not ready", cut.Find("[data-testid=format-card-StoryReveal]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=format-card-StoryReveal] input[type=checkbox]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=hook-StoryReveal]"));
    }

    [Fact]
    public void AgentCanRemoveSwapAndMakeEveryEligibleVideo()
    {
        var plan = RichPlan();
        var picker = new VerticalVideoPicker(plan.Recommendation);
        var cut = Render(plan, picker);
        var first = picker.Recommended[0];

        cut.Find($"[data-testid=format-card-{first}] input[type=checkbox]").Change(false);
        Assert.DoesNotContain(first, picker.Chosen);
        Assert.Contains("You'll get 2 vertical videos.", cut.Find("[data-testid=vertical-estimate]").TextContent, StringComparison.Ordinal);
        Assert.Equal(VerticalVideoSelection.AgentChosen, picker.ToRequest(null).Selection);

        cut.Find("[data-testid=use-all-eligible]").Click();
        Assert.Equal(picker.Eligible, picker.Chosen);
        Assert.Contains($"You'll get {picker.Eligible.Count} vertical videos.", cut.Find("[data-testid=vertical-estimate]").TextContent,
            StringComparison.Ordinal);

        cut.Find("[data-testid=use-recommended]").Click();
        Assert.Equal(picker.Recommended, picker.Chosen);
        Assert.NotEmpty(cut.FindAll($"[data-testid=swap-{first}]"));
    }

    [Fact]
    public void UntickingEveryVideoAsksForAPickAndTheGoalIsReported()
    {
        var plan = RichPlan();
        var picker = new VerticalVideoPicker(plan.Recommendation);
        VerticalCampaignGoal? chosenGoal = null;
        var cut = Render(plan, picker, goal => chosenGoal = goal);

        foreach (var format in picker.Chosen.ToArray())
        {
            cut.Find($"[data-testid=format-card-{format}] input[type=checkbox]").Change(false);
        }

        Assert.True(cut.Instance.NothingPicked);
        Assert.Contains("Pick at least one video", cut.Find("[data-testid=vertical-estimate]").TextContent, StringComparison.Ordinal);

        cut.Find("input[data-testid=goal-GuessThePrice]").Click();
        Assert.Equal(VerticalCampaignGoal.GuessThePrice, chosenGoal);
    }

    [Fact]
    public void ListingsWithIncompleteMediaGetAPlainFallback()
    {
        var loading = Render(null, null);
        Assert.NotNull(loading.Find("[data-testid=vertical-plan-loading]"));
        Assert.Empty(loading.FindAll("[data-testid=vertical-estimate]"));

        var bare = RecipeFixture.Input() with { Photos = [], Footage = [] };
        var plan = VerticalCreativePlan.Create(bare);
        var picker = new VerticalVideoPicker(plan.Recommendation);
        var cut = Render(plan, picker);

        Assert.Empty(picker.Eligible);
        Assert.Contains("one shared vertical video", cut.Find("[data-testid=vertical-none-eligible]").TextContent, StringComparison.Ordinal);
        Assert.Contains("You'll get 1 shared vertical video.", cut.Find("[data-testid=vertical-estimate]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Instance.NothingPicked);
        Assert.All(cut.FindAll("input[type=checkbox]"), box => Assert.True(box.HasAttribute("disabled")));
    }

    [Fact]
    public void FormatCardsAreLabelledForScreenReaders()
    {
        var plan = RichPlan();
        var cut = Render(plan, new VerticalVideoPicker(plan.Recommendation));

        foreach (var card in cut.FindAll("li.vertical-format"))
        {
            var headingId = card.GetAttribute("aria-labelledby");
            Assert.False(string.IsNullOrEmpty(headingId));
            Assert.NotNull(cut.Find($"#{headingId}"));
        }

        Assert.NotNull(cut.Find("[aria-label='Vertical video formats']"));
        Assert.Equal("polite", cut.Find("[data-testid=vertical-estimate]").GetAttribute("aria-live"));
        Assert.All(cut.FindAll(".vertical-format-preview img"), image => Assert.StartsWith("Opening photo", image.GetAttribute("alt")));
    }
}
