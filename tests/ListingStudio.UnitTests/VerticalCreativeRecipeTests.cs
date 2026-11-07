using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VerticalMediaRoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnalysisSuggestsOneHeroAndDescriptiveRolesButNeverClaims()
    {
        var suggested = VerticalMediaRoles.Suggest(RecipeFixture.RolePhotos(), RecipeFixture.RoleFootage());

        Assert.Equal([RecipeFixture.Kitchen], suggested.Where(pair => pair.Value.Contains(VerticalMediaRole.HeroFeature)).Select(pair => pair.Key));
        Assert.Contains(VerticalMediaRole.FeatureDetail, suggested[RecipeFixture.Living]);
        Assert.Contains(VerticalMediaRole.Exterior, suggested[RecipeFixture.Front]);
        Assert.Contains(VerticalMediaRole.Lifestyle, suggested[RecipeFixture.Patio]);
        Assert.Single(suggested, pair => pair.Value.Contains(VerticalMediaRole.ClosingView));
        Assert.Contains(VerticalMediaRole.HostedWalkthrough, suggested[RecipeFixture.WalkVideo]);
        Assert.All(suggested.Values.SelectMany(roles => roles),
            role => Assert.DoesNotContain(role, PropertyMediaRoleAssignment.AgentOnlyRoles));
    }

    [Fact]
    public void SilentOrShortFootageIsNotSuggestedAsHosted()
    {
        var suggested = VerticalMediaRoles.Suggest([], [
            new VerticalRoleFootage(RecipeFixture.WalkVideo, 60_000, true, false, null),
            new VerticalRoleFootage(RecipeFixture.IntroVideo, 8_000, true, true, null),
        ]);

        Assert.Empty(suggested);
    }

    [Fact]
    public void AgentDecisionsWinAndTheLatestDecisionIsInForce()
    {
        var suggested = new HashSet<VerticalMediaRole> { VerticalMediaRole.HeroFeature, VerticalMediaRole.Exterior };
        var target = Guid.NewGuid();
        VerticalMediaRoleOverride Decide(VerticalMediaRole role, VerticalMediaRoleDecision decision, int minute) =>
            new(target, role, decision, "agent", Now.AddMinutes(minute));

        var removed = VerticalMediaRoles.Resolve(suggested, [Decide(VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed, 1)]);
        Assert.DoesNotContain(removed, state => state.Role == VerticalMediaRole.HeroFeature);
        Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.Exterior, VerticalMediaRoleSource.Suggested), removed);

        var reAdded = VerticalMediaRoles.Resolve(suggested, [
            Decide(VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, 2),
            Decide(VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed, 1),
            Decide(VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned, 3),
        ]);
        Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.HeroFeature, VerticalMediaRoleSource.Agent), reAdded);
        Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.AfterRenovation, VerticalMediaRoleSource.Agent), reAdded);

        var cleared = VerticalMediaRoles.Resolve(suggested, [
            Decide(VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed, 1),
            Decide(VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Cleared, 2),
        ]);
        Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.HeroFeature, VerticalMediaRoleSource.Suggested), cleared);

        var smuggled = VerticalMediaRoles.Resolve(new HashSet<VerticalMediaRole> { VerticalMediaRole.BeforeRenovation }, []);
        Assert.Empty(smuggled);
    }

    [Fact]
    public void RoleDecisionsNeedOneTargetAndFootageRolesStayOnVideos()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => PropertyMediaRoleAssignment.Create(org, property, null, null,
            VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, "agent", Now));
        Assert.Throws<ArgumentException>(() => PropertyMediaRoleAssignment.Create(org, property, Guid.NewGuid(), Guid.NewGuid(),
            VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, "agent", Now));
        Assert.Throws<ArgumentException>(() => PropertyMediaRoleAssignment.Create(org, property, Guid.NewGuid(), null,
            VerticalMediaRole.HostedWalkthrough, VerticalMediaRoleDecision.Assigned, "agent", Now));
        Assert.Throws<ArgumentException>(() => PropertyMediaRoleAssignment.Create(org, property, Guid.NewGuid(), null,
            VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, " ", Now));
        var video = PropertyMediaRoleAssignment.Create(org, property, null, Guid.NewGuid(),
            VerticalMediaRole.AgentIntro, VerticalMediaRoleDecision.Assigned, "agent", Now);
        Assert.Equal("agent", video.SetByUserId);
        Assert.Equal(Now, video.SetAtUtc);
    }

    [Fact]
    public void ClaimsAreOneCleanLineInTheAgentsWordsAndCanExpireOrBeWithdrawn()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        var story = ListingCreativeClaim.ConfirmStory(org, property, "  The kitchen\nwas closed​ off. ", "Now it opens to the yard.", "agent", Now);
        Assert.Equal("The kitchen was closed off.", story.PrimaryText);
        Assert.True(story.IsActiveAt(Now));
        Assert.Throws<ArgumentException>(() => ListingCreativeClaim.ConfirmStory(org, property, " ", "Payoff", "agent", Now));
        Assert.Throws<ArgumentException>(() => ListingCreativeClaim.ConfirmStory(org, property, new string('a', 101), "Payoff", "agent", Now));
        Assert.Throws<ArgumentException>(() => ListingCreativeClaim.ConfirmAccess(org, property, "Private preview", Now.AddMinutes(-1), "agent", Now));

        var access = ListingCreativeClaim.ConfirmAccess(org, property, "Private preview Saturday only", Now.AddDays(2), "agent", Now);
        Assert.True(access.IsActiveAt(Now.AddDays(1)));
        Assert.False(access.IsActiveAt(Now.AddDays(3)));
        Assert.False(ListingCreativeClaim.Withdraw(org, property, ListingCreativeClaimKind.StoryReveal, "agent", Now).IsActiveAt(Now));
    }
}

public sealed class VerticalReadinessTests
{
    [Fact]
    public void ReadinessAlwaysAgreesWithEligibility()
    {
        VerticalPlanningInput[] cases =
        [
            RecipeFixture.Input(),
            RecipeFixture.Input() with { Photos = [] },
            RecipeFixture.Input() with { Footage = [] },
            RecipeFixture.Rich(),
            RecipeFixture.Input() with { Facts = RecipeFixture.Facts() with { ListingPrice = 0 } },
            RecipeFixture.Input() with { Presenter = new VerticalPresenterInput(true, true) },
        ];

        foreach (var input in cases)
        {
            var recommendation = VerticalFormatRules.Evaluate(input.ToRecommendationInputs());
            foreach (var readiness in VerticalReadinessEvaluator.Evaluate(input, recommendation))
            {
                Assert.Equal(recommendation.For(readiness.Format).IsEligible, readiness.IsReady);
                Assert.NotEmpty(readiness.Items);
                Assert.All(readiness.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Explanation)));
            }
        }
    }

    [Fact]
    public void ReadinessSeparatesReadyMissingAndUnverified()
    {
        var input = RecipeFixture.Input() with
        {
            Presenter = new VerticalPresenterInput(true, false),
            Mentions = new VerticalUnconfirmedMentions(Renovation: true, Exclusivity: true),
        };
        var readiness = Readiness(input);

        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.PriceFirst, "Listing price").State);
        Assert.Equal(VerticalReadinessState.Missing, Item(readiness, VerticalCreativeFormat.PriceFirst, "Value message").State);
        Assert.False(Item(readiness, VerticalCreativeFormat.PriceFirst, "Value message").IsRequired);
        Assert.Equal(VerticalReadinessState.Unverified, Item(readiness, VerticalCreativeFormat.HostedPov, "Consented presenter").State);
        Assert.Equal(VerticalReadinessState.Unverified, Item(readiness, VerticalCreativeFormat.HostedPov, "Host footage").State);
        Assert.Equal(VerticalReadinessState.Unverified, Item(readiness, VerticalCreativeFormat.StoryReveal, "Approved story claim").State);
        Assert.Equal(VerticalReadinessState.Unverified, Item(readiness, VerticalCreativeFormat.ExclusiveAccess, "Confirmed access language").State);
        Assert.Equal(VerticalReadinessState.Unverified, Item(readiness, VerticalCreativeFormat.WowFeature, "Opening shot").State);
        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.WowFeature, "Visually strong feature").State);
    }

    [Fact]
    public void AgentConfirmationsTurnUnverifiedIntoReady()
    {
        var readiness = Readiness(RecipeFixture.Rich());

        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.WowFeature, "Opening shot").State);
        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.HostedPov, "Host footage").State);
        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.StoryReveal, "Approved story claim").State);
        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.StoryReveal, "Before and after photos").State);
        Assert.Equal(VerticalReadinessState.Ready, Item(readiness, VerticalCreativeFormat.PriceFirst, "Value message").State);
        Assert.All(readiness, item => Assert.Equal(item.Format != VerticalCreativeFormat.ExclusiveAccess, item.IsReady));
    }

    [Fact]
    public void PendingAnalysisIsUnverifiedNotMissing()
    {
        var pending = RecipeFixture.Input() with
        {
            Photos = RecipeFixture.Input().Photos.Select(photo => photo with { AnalysisCompleted = false, HeroScore = null, QualityScore = null, Roles = [] }).ToArray(),
        };

        var item = Item(Readiness(pending), VerticalCreativeFormat.WowFeature, "Visually strong feature");
        Assert.Equal(VerticalReadinessState.Unverified, item.State);
        Assert.Contains("waiting for analysis", item.Explanation, StringComparison.Ordinal);
    }

    private static IReadOnlyList<VerticalFormatReadiness> Readiness(VerticalPlanningInput input) =>
        VerticalReadinessEvaluator.Evaluate(input, VerticalFormatRules.Evaluate(input.ToRecommendationInputs()));

    private static VerticalReadinessItem Item(IReadOnlyList<VerticalFormatReadiness> readiness, VerticalCreativeFormat format, string input) =>
        readiness.Single(item => item.Format == format).Items.Single(item => item.Input == input);
}

public sealed class VerticalCreativeRecipeTests
{
    private static readonly VerticalCreativeFormat[] FirstRelease =
    [
        VerticalCreativeFormat.WowFeature,
        VerticalCreativeFormat.PriceFirst,
        VerticalCreativeFormat.HostedPov,
        VerticalCreativeFormat.StoryReveal,
    ];

    [Fact]
    public void FirstReleaseFormatsAreMateriallyDifferent()
    {
        var recipes = VerticalRecipePlanner.Plan(RecipeFixture.Rich(), FirstRelease);

        Assert.Equal(FirstRelease, recipes.Select(recipe => recipe.Format));
        Assert.Equal(4, recipes.Select(recipe => recipe.Beats[0].Kind).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => string.Join(",", recipe.Beats.Select(beat => beat.Kind))).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.Hook.GroundingKey).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.Narration).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.CaptionDensity).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.CallToAction.Text).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.Purpose).Distinct().Count());
        Assert.Equal(4, recipes.Select(recipe => recipe.PrimaryPropertyMediaId ?? recipe.PrimaryPropertyVideoId).Distinct().Count());
    }

    [Fact]
    public void EveryRecipeIsContiguousGroundedAndAcceptedByTheLibrary()
    {
        var input = RecipeFixture.Rich() with { AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private preview this Saturday") };
        var recipes = VerticalRecipePlanner.Plan(input, VerticalFormatRules.Formats);
        var library = CampaignVideoLibrary.Create(Guid.NewGuid(), input.PropertyId, Guid.NewGuid(),
            VerticalVideoSelection.AllEligible, VerticalFormatRules.Version, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(VerticalFormatRules.Formats.Count, recipes.Count);
        for (var index = 0; index < recipes.Count; index++)
        {
            var recipe = recipes[index];
            Assert.Empty(recipe.Validate());
            var keys = recipe.Facts.Select(fact => fact.Key).ToHashSet();
            Assert.All(recipe.Beats.SelectMany(beat => new[] { beat.Overlay, beat.Narration }).OfType<VerticalRecipeLine>(),
                line => Assert.Contains(line.GroundingKey, keys));
            Assert.All(recipe.Facts, fact => Assert.Equal(fact.Value, recipe.Facts.Single(other => other.Key == fact.Key).Value));
            Assert.Equal(recipe, VerticalCreativeRecipe.FromJson(recipe.ToJson()) is { } restored
                && restored.ToJson() == recipe.ToJson() ? recipe : null);
            var variant = library.AddVariant(recipe.ToVariantPlan(index + 1), DateTimeOffset.UnixEpoch);
            Assert.Equal(recipe.RecipeVersion, variant.RecipeVersion);
            Assert.Equal(CampaignVideoVariantStatus.Planned, variant.Status);
        }
    }

    [Fact]
    public void WowFeatureOpensOnTheAgentsHeroAndLoopsInFifteenSeconds()
    {
        var input = RecipeFixture.Input(heroOverride: RecipeFixture.Patio);
        var recipe = new WowFeaturePlanner().Plan(input)!;

        Assert.Equal(15, recipe.DurationSeconds);
        Assert.True(recipe.Loops);
        Assert.Equal(RecipeFixture.Patio, recipe.PrimaryPropertyMediaId);
        Assert.Equal(RecipeFixture.Patio, recipe.Beats[0].PropertyMediaId);
        Assert.Equal(recipe.Beats[0].PropertyMediaId, recipe.Beats[^1].PropertyMediaId);
        Assert.Equal(VerticalBeatKind.Hook, recipe.Beats[0].Kind);
        Assert.Equal(VerticalBeatKind.LoopReturn, recipe.Beats[^1].Kind);
        Assert.Equal("Look at this patio.", recipe.Hook.Text);
        Assert.Equal(VerticalNarrationStructure.MusicOnly, recipe.Narration);
        Assert.All(recipe.Beats, beat => Assert.Null(beat.Narration));
        Assert.Equal("Save this for later", recipe.CallToAction.Text);
        Assert.Empty(recipe.Fallbacks);
        Assert.Equal(CampaignVoiceoverMode.None, recipe.ToVariantPlan(1).VoiceoverMode);
    }

    [Fact]
    public void WowFeatureFallsBackToTheStrongestFeatureWhenTheAgentRemovedTheSuggestion()
    {
        var input = RecipeFixture.Input(removeHero: true);
        var recipe = new WowFeaturePlanner().Plan(input)!;

        Assert.Equal(RecipeFixture.Kitchen, recipe.PrimaryPropertyMediaId);
        Assert.Contains(recipe.Fallbacks, reason => reason.Contains("highest-scoring", StringComparison.Ordinal));
        Assert.Null(new WowFeaturePlanner().Plan(input with { Photos = input.Photos.Select(photo => photo with { HeroScore = 40 }).ToArray() }));
    }

    [Fact]
    public void PriceFirstLeadsWithVerifiedPriceCityAndFactsThenWalksInOrder()
    {
        var recipe = new PriceFirstPlanner().Plan(RecipeFixture.Input() with { VerifiedPriceChange = 10_000 })!;

        Assert.Equal(30, recipe.DurationSeconds);
        Assert.Equal(RecipeFixture.Front, recipe.PrimaryPropertyMediaId);
        Assert.Equal([VerticalBeatKind.PriceReveal, VerticalBeatKind.EssentialFacts, VerticalBeatKind.ValueMessage],
            recipe.Beats.Take(3).Select(beat => beat.Kind));
        Assert.Equal("$450,000 in Raleigh, NC", recipe.Hook.Text);
        Assert.Equal("property.priceCityState", recipe.Hook.GroundingKey);
        Assert.Equal("3 beds • 2.5 baths • 2,100 sq ft", recipe.Beats[1].Overlay!.Text);
        Assert.Equal("Price reduced $10,000", recipe.Beats[2].Overlay!.Text);
        Assert.Equal(VerticalBeatKind.PriceRecap, recipe.Beats[^1].Kind);
        Assert.Equal("Ask for the full listing details", recipe.CallToAction.Text);
        var tour = recipe.Beats.Where(beat => beat.Kind == VerticalBeatKind.RoomTour).ToArray();
        Assert.NotEmpty(tour);
        Assert.All(tour, beat => Assert.NotNull(beat.Overlay));
        var order = RecipeFixture.Input().Photos.ToDictionary(photo => photo.MediaId, photo => photo.DisplayOrder);
        Assert.Equal(tour.Select(beat => order[beat.PropertyMediaId!.Value]).Order(), tour.Select(beat => order[beat.PropertyMediaId!.Value]));
        Assert.All(recipe.Facts.Where(fact => fact.Key.StartsWith("property.", StringComparison.Ordinal)),
            fact => Assert.Equal(VerticalCopySource.VerifiedProperty, fact.Source));
    }

    [Fact]
    public void PriceFirstWithoutAPriceChangeHasNoValueBeatAndNeedsVerifiedFacts()
    {
        var recipe = new PriceFirstPlanner().Plan(RecipeFixture.Input())!;
        Assert.DoesNotContain(recipe.Beats, beat => beat.Kind == VerticalBeatKind.ValueMessage);
        Assert.DoesNotContain(recipe.Facts, fact => fact.Key == "campaign.priceReduction");
        Assert.Null(new PriceFirstPlanner().Plan(RecipeFixture.Input() with { Facts = RecipeFixture.Facts() with { City = null } }));
        Assert.Null(new PriceFirstPlanner().Plan(RecipeFixture.Input() with { Facts = RecipeFixture.Facts() with { Bedrooms = 0 } }));
    }

    [Fact]
    public void HostedPovOpensOnTheIntroClipWithTheAgentsFirstSentence()
    {
        var recipe = new HostedPovPlanner().Plan(RecipeFixture.Rich())!;

        Assert.Equal(VerticalBeatKind.HostIntro, recipe.Beats[0].Kind);
        Assert.Equal(RecipeFixture.IntroVideo, recipe.Beats[0].PropertyVideoId);
        Assert.Equal(RecipeFixture.IntroVideo, recipe.PrimaryPropertyVideoId);
        Assert.True(recipe.Beats[0].KeepsSourceAudio);
        Assert.Equal("Welcome to my favorite listing this fall.", recipe.Hook.Text);
        Assert.Equal("room.narration.1", recipe.Hook.GroundingKey);
        Assert.Contains(recipe.Beats, beat => beat.Kind == VerticalBeatKind.HostedWalk && beat.PropertyVideoId == RecipeFixture.WalkVideo);
        Assert.Equal(VerticalCaptionDensity.WordTimed, recipe.CaptionDensity);
        Assert.Equal("Message me to see it in person", recipe.CallToAction.Text);
    }

    [Fact]
    public void HostedPovWithoutFootageUsesTheConsentedPresenterAndNarrationOnMatchingStops()
    {
        var input = RecipeFixture.Input() with
        {
            Footage = [],
            Presenter = new VerticalPresenterInput(true, true),
            Narration = RecipeFixture.Narration(),
        };
        var recipe = new HostedPovPlanner().Plan(input)!;

        Assert.True(recipe.UsesPresenter);
        Assert.Equal(VerticalVisualTreatment.Presenter, recipe.Beats[0].Treatment);
        Assert.Equal(VerticalVisualTreatment.Presenter, recipe.Beats[^1].Treatment);
        var kitchenLine = recipe.Beats.Single(beat => beat.Narration?.GroundingKey == "room.narration.2");
        Assert.Equal(RecipeFixture.Kitchen, kitchenLine.PropertyMediaId);
        Assert.All(recipe.Facts.Where(fact => fact.Key.StartsWith("room.", StringComparison.Ordinal)),
            fact => Assert.Equal(VerticalCopySource.AgentNarration, fact.Source));
    }

    [Fact]
    public void HostedPovNeverUsesAnUnconsentedPresenterAndFallsBackToATemplateHook()
    {
        var input = RecipeFixture.Input() with
        {
            Footage = [],
            Presenter = new VerticalPresenterInput(true, false),
            Narration = [new VerticalNarrationLine("acceptedScript.segment.1", "This home has a long story. It starts with the porch and goes on through every room, the yard and the street outside it.", null)],
        };
        var recipe = new HostedPovPlanner().Plan(input)!;

        Assert.False(recipe.UsesPresenter);
        Assert.DoesNotContain(recipe.Beats, beat => beat.Treatment == VerticalVisualTreatment.Presenter);
        Assert.Equal("template.hook.hostedPov", recipe.Hook.GroundingKey);
        Assert.Contains(recipe.Fallbacks, reason => reason.Contains("No host on camera", StringComparison.Ordinal));
        Assert.Null(new HostedPovPlanner().Plan(RecipeFixture.Input() with { Footage = [] }));
    }

    [Fact]
    public void StoryRevealFollowsProblemTensionResultPayoffInFortyFiveSeconds()
    {
        var recipe = new StoryRevealPlanner().Plan(RecipeFixture.Rich())!;

        Assert.Equal(45, recipe.DurationSeconds);
        var kinds = recipe.Beats.Select(beat => beat.Kind).Distinct().ToArray();
        Assert.Equal([VerticalBeatKind.Problem, VerticalBeatKind.Tension, VerticalBeatKind.Result, VerticalBeatKind.Payoff, VerticalBeatKind.CallToAction], kinds);
        Assert.Equal(RecipeFixture.Before1, recipe.Beats[0].PropertyMediaId);
        Assert.Equal("claim.story.problem", recipe.Hook.GroundingKey);
        Assert.Equal("The kitchen was closed off and dark.", recipe.Hook.Text);
        Assert.Equal(RecipeFixture.After1, recipe.PrimaryPropertyMediaId);
        Assert.Equal("claim.story.payoff", recipe.Beats.Single(beat => beat.Kind == VerticalBeatKind.Payoff).Overlay!.GroundingKey);
        Assert.All(recipe.Facts.Where(fact => fact.Key.StartsWith("claim.", StringComparison.Ordinal)),
            fact => Assert.Equal(VerticalCopySource.AgentClaim, fact.Source));
    }

    [Fact]
    public void StoryRevealWithoutBeforePhotosShowsTheProblemAsATextCardInThirtySeconds()
    {
        var input = RecipeFixture.Rich() with
        {
            Photos = RecipeFixture.Rich().Photos.Where(photo => !photo.Has(VerticalMediaRole.BeforeRenovation)).ToArray(),
        };
        var recipe = new StoryRevealPlanner().Plan(input)!;

        Assert.Equal(30, recipe.DurationSeconds);
        Assert.Equal(VerticalVisualTreatment.TextCardOverPhoto, recipe.Beats[0].Treatment);
        Assert.Contains(recipe.Fallbacks, reason => reason.Contains("text card", StringComparison.Ordinal));
        Assert.Null(new StoryRevealPlanner().Plan(input with { StoryClaim = null }));
    }

    [Fact]
    public void ExclusiveAccessUsesOnlyTheConfirmedAccessLanguage()
    {
        Assert.Null(new ExclusiveAccessPlanner().Plan(RecipeFixture.Rich()));
        Assert.Null(new ExclusiveAccessPlanner().Plan(RecipeFixture.Rich() with
        {
            AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private preview"),
            Footage = [],
        }));

        var recipe = new ExclusiveAccessPlanner().Plan(RecipeFixture.Rich() with
        {
            AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private preview this Saturday"),
        })!;
        Assert.Equal("Private preview this Saturday", recipe.Hook.Text);
        Assert.Equal("claim.access.text", recipe.Hook.GroundingKey);
        Assert.Equal(VerticalBeatKind.AccessHook, recipe.Beats[0].Kind);
        Assert.Equal("Message me to request access", recipe.CallToAction.Text);
        Assert.DoesNotContain(recipe.Facts, fact => fact.Source == VerticalCopySource.MediaAnalysis);
    }

    [Fact]
    public void BeforePhotosAppearOnlyInStoryReveal()
    {
        var input = RecipeFixture.Rich() with { AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private preview") };
        foreach (var recipe in VerticalRecipePlanner.Plan(input, VerticalFormatRules.Formats))
        {
            var shown = recipe.Beats.Select(beat => beat.PropertyMediaId).Append(recipe.PrimaryPropertyMediaId).ToArray();
            var hasBefore = shown.Contains(RecipeFixture.Before1) || shown.Contains(RecipeFixture.Before2);
            Assert.Equal(recipe.Format == VerticalCreativeFormat.StoryReveal, hasBefore);
        }
    }

    [Fact]
    public void PlanIncludesRecipesOnlyForEligibleFormats()
    {
        var plain = VerticalCreativePlan.Create(RecipeFixture.Input());
        Assert.Equal(plain.Recommendation.AllEligible.Select(item => item.Format), plain.Recipes.Select(recipe => recipe.Format));
        Assert.DoesNotContain(plain.Recipes, recipe => recipe.Format is VerticalCreativeFormat.StoryReveal or VerticalCreativeFormat.ExclusiveAccess);

        var rich = VerticalCreativePlan.Create(RecipeFixture.Rich());
        Assert.Contains(rich.Recipes, recipe => recipe.Format == VerticalCreativeFormat.StoryReveal);
        Assert.True(rich.Recommendation.For(VerticalCreativeFormat.StoryReveal).IsEligible);
    }

    [Fact]
    public void RecipesAreDeterministic()
    {
        var first = VerticalRecipePlanner.Plan(RecipeFixture.Rich(), FirstRelease).Select(recipe => recipe.ToJson()).ToArray();
        var second = VerticalRecipePlanner.Plan(RecipeFixture.Rich(), FirstRelease).Select(recipe => recipe.ToJson()).ToArray();
        Assert.Equal(first, second);
    }

    /// <summary>Writes one serialized recipe per format for review when RECIPE_SAMPLES_DIR is set.</summary>
    [Fact]
    public void WriteRepresentativeRecipes()
    {
        var directory = Environment.GetEnvironmentVariable("RECIPE_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var input = RecipeFixture.Rich() with { AccessClaim = new VerticalAccessClaimInput(Guid.Parse("00000000-0000-0000-0000-0000000000ac"), "Private preview this Saturday") };
        var plan = VerticalCreativePlan.Create(input);
        File.WriteAllText(Path.Combine(directory, "recommendation.json"), plan.Recommendation.ToSnapshotJson());
        foreach (var recipe in plan.Recipes)
        {
            File.WriteAllText(Path.Combine(directory, $"{recipe.RecipeVersion}.json"), recipe.ToIndentedJson());
        }
    }
}
