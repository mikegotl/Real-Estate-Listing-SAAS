using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>The TikTok campaign types: each leads with its own video and opens with its own hook.</summary>
public sealed class VerticalCampaignTypeTests
{
    private static VerticalCreativePlan PlanFor(VerticalCampaignGoal? goal) =>
        VerticalCreativePlan.Create(RecipeFixture.Input() with { Goal = goal });

    [Theory]
    [InlineData(VerticalCampaignGoal.WhatPriceBuys, VerticalCreativeFormat.PriceFirst)]
    [InlineData(VerticalCampaignGoal.GuessThePrice, VerticalCreativeFormat.PriceFirst)]
    [InlineData(VerticalCampaignGoal.StandoutRoom, VerticalCreativeFormat.WowFeature)]
    [InlineData(VerticalCampaignGoal.SpeedTour, VerticalCreativeFormat.SpeedTour)]
    [InlineData(VerticalCampaignGoal.LivingInTown, VerticalCreativeFormat.NeighborhoodTour)]
    public void EachTypeMakesItsOwnVideoFirst(VerticalCampaignGoal goal, VerticalCreativeFormat lead)
    {
        var plan = PlanFor(goal);

        var first = plan.Recommendation.Formats[0];
        Assert.Equal(lead, first.Format);
        Assert.True(first.IsRecommended);
        Assert.Equal(1, first.Rank);
        Assert.Contains("Made first because it is the campaign type you picked.", first.Reasons);
        Assert.DoesNotContain(first.Reasons, reason => reason.StartsWith("Not in the default set", StringComparison.Ordinal));
        Assert.InRange(plan.Recommendation.Recommended.Count, 1, 3);
        Assert.Equal(Enumerable.Range(1, plan.Recommendation.Formats.Count), plan.Recommendation.Formats.Select(item => item.Rank));
    }

    [Fact]
    public void ATypeWhoseVideoCannotBeMadeLeavesTheRecommendationAlone()
    {
        var withoutGoal = PlanFor(null);
        // No confirmed host footage in the plain listing, so the agent-hosted video can't lead.
        var hosted = PlanFor(VerticalCampaignGoal.AgentHosted);

        Assert.False(hosted.Recommendation.For(VerticalCreativeFormat.HostedPov).IsEligible);
        Assert.Equal(withoutGoal.Recommendation.Recommended.Select(item => item.Format),
            hosted.Recommendation.Recommended.Select(item => item.Format));
    }

    [Fact]
    public void PriceHooksFollowTheCampaignType()
    {
        Recipe(VerticalCampaignGoal.WhatPriceBuys, VerticalCreativeFormat.PriceFirst, out var buys);
        Assert.Equal("What $450,000 buys in Raleigh, NC", buys.Hook.Text);

        Recipe(VerticalCampaignGoal.GuessThePrice, VerticalCreativeFormat.PriceFirst, out var guess);
        Assert.Equal("Guess the price of this Raleigh, NC home", guess.Hook.Text);
        Assert.DoesNotContain("$", guess.Hook.Text, StringComparison.Ordinal);
        Assert.Equal("Did you guess right? Ask for the details", guess.CallToAction.Text);
        Assert.Empty(guess.Validate());
    }

    [Fact]
    public void StandoutRoomTeasesTheHeroRoom()
    {
        Recipe(VerticalCampaignGoal.StandoutRoom, VerticalCreativeFormat.WowFeature, out var wow);
        Assert.StartsWith("Wait till you see the ", wow.Hook.Text, StringComparison.Ordinal);

        Recipe(null, VerticalCreativeFormat.WowFeature, out var plain);
        Assert.StartsWith("Look at this", plain.Hook.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedTourIsFifteenSecondsOfQuickCuts()
    {
        var recipe = new SpeedTourPlanner().Plan(RecipeFixture.Input())!;

        Assert.Empty(recipe.Validate());
        Assert.Equal(15, recipe.DurationSeconds);
        Assert.Equal(15_000, recipe.Beats.Sum(beat => beat.DurationMs));
        var shots = recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.SpeedShot).ToArray();
        Assert.True(shots.Length >= 4);
        Assert.All(shots, shot => Assert.InRange(shot.DurationMs, 500, 2_000));
        Assert.Equal("15-second tour: $450,000 in Raleigh, NC", recipe.Hook.Text);
        Assert.Equal(VerticalNarrationStructure.MusicOnly, recipe.Narration);

        var fewPhotos = RecipeFixture.Input() with { Photos = RecipeFixture.Input().Photos.Take(5).ToArray() };
        Assert.Null(new SpeedTourPlanner().Plan(fewPhotos));
    }

    [Fact]
    public void LivingInTownNamesOnlyApprovedPlacesWithWholeMileDistances()
    {
        var recipe = new NeighborhoodTourPlanner().Plan(RecipeFixture.Input())!;

        Assert.Empty(recipe.Validate());
        Assert.Equal("Living in Raleigh, NC", recipe.Hook.Text);
        var places = recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.NearbyPlace).Select(beat => beat.Overlay!.Text).ToArray();
        Assert.Equal(["Pullen Park, less than a mile away", "Five Points Farmers Market, about 2 miles away"], places);
        Assert.All(recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.NearbyPlace), beat =>
            Assert.Equal(VerticalCopySource.ApprovedNeighborhood, recipe.Facts.Single(fact => fact.Key == beat.Overlay!.GroundingKey).Source));

        Assert.Null(new NeighborhoodTourPlanner().Plan(RecipeFixture.Input() with { NeighborhoodPlaces = RecipeFixture.Places().Take(1).ToArray() }));
        Assert.Null(new NeighborhoodTourPlanner().Plan(RecipeFixture.Input() with { Facts = RecipeFixture.Facts() with { City = null } }));
    }

    [Fact]
    public void NeighborhoodTourNeedsTwoApprovedPlaces()
    {
        var one = VerticalCreativePlan.Create(RecipeFixture.Input() with
        {
            Facts = RecipeFixture.Facts() with { ApprovedNeighborhoodPlaces = 1 },
        });

        Assert.False(one.Recommendation.For(VerticalCreativeFormat.NeighborhoodTour).IsEligible);
        Assert.DoesNotContain(one.Recipes, recipe => recipe.Format == VerticalCreativeFormat.NeighborhoodTour);
    }

    private static void Recipe(VerticalCampaignGoal? goal, VerticalCreativeFormat format, out VerticalCreativeRecipe recipe) =>
        recipe = PlanFor(goal).Recipes.Single(candidate => candidate.Format == format);
}
