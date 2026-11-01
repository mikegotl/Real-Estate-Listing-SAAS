using System.Text.Json;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.TestSupport;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VerticalVariantGenerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LibraryStagesRunBetweenTheStagesTheyServeDespiteTheirAppendedValues()
    {
        Assert.True(CampaignGenerationStageOrder.Rank(CampaignGenerationStage.GenerateNarration)
            < CampaignGenerationStageOrder.Rank(CampaignGenerationStage.PlanVerticalLibrary));
        Assert.True(CampaignGenerationStageOrder.Rank(CampaignGenerationStage.PlanVerticalLibrary)
            < CampaignGenerationStageOrder.Rank(CampaignGenerationStage.GenerateDerivativePlans));
        Assert.True(CampaignGenerationStageOrder.Rank(CampaignGenerationStage.RenderLongForm)
            < CampaignGenerationStageOrder.Rank(CampaignGenerationStage.GenerateVariantNarration));
        Assert.True(CampaignGenerationStageOrder.Rank(CampaignGenerationStage.FinalizeVerticalLibrary)
            < CampaignGenerationStageOrder.Rank(CampaignGenerationStage.GenerateSocialCopy));
        Assert.Equal(
            Enum.GetValues<CampaignGenerationStage>().Length,
            Enum.GetValues<CampaignGenerationStage>().Select(CampaignGenerationStageOrder.Rank).Distinct().Count());

        // Persisted values stay where they were: the library stages are appended after FinalizeCampaign.
        Assert.Equal(12, (int)CampaignGenerationStage.FinalizeCampaign);
        Assert.Equal(13, (int)CampaignGenerationStage.PlanVerticalLibrary);
        Assert.Equal(17, (int)CampaignGenerationStage.FinalizeVerticalLibrary);

        // A legacy job shows exactly its old stages.
        Assert.DoesNotContain(
            CampaignGenerationStageOrder.For(false, CampaignGenerationStage.RenderHero),
            CampaignGenerationStageOrder.IsVerticalLibraryStage);
        Assert.Equal(18, CampaignGenerationStageOrder.For(true, CampaignGenerationStage.RenderHero).Count);
    }

    [Fact]
    public void CampaignMovesIntoAndOutOfTheLibraryStagesButNeverBackwards()
    {
        var job = RunningJob(CampaignGenerationStage.RenderLongForm);
        job.CompleteStage(CampaignGenerationStage.GenerateVariantNarration, Now);
        Begin(job);
        job.CompleteStage(CampaignGenerationStage.RenderVariants, Now);
        Begin(job);
        Assert.Throws<InvalidOperationException>(() => job.CompleteStage(CampaignGenerationStage.RenderHero, Now));
        job.CompleteStage(CampaignGenerationStage.FinalizeVerticalLibrary, Now);
        Begin(job);
        job.CompleteStage(CampaignGenerationStage.GenerateSocialCopy, Now);
        Assert.Equal(CampaignGenerationStage.GenerateSocialCopy, job.CurrentStage);
    }

    [Fact]
    public void LibraryCanOnlyBeRequestedBeforeTheCampaignStartsAndWithAVerticalOutput()
    {
        var landscapeOnly = CampaignGenerationJob.Create(Guid.NewGuid(), Guid.NewGuid(), "user", new string('a', 64),
            CampaignOutputSelection.YouTubeTour, Now, CampaignCreativeOptions.SocialDefault);
        Assert.Throws<InvalidOperationException>(() => landscapeOnly.RequestVerticalLibrary(VerticalVideoSelection.RecommendedThree));
        var job = CampaignGenerationJob.Create(Guid.NewGuid(), Guid.NewGuid(), "user", new string('a', 64),
            CampaignOutputSelection.TikTok, Now, CampaignCreativeOptions.SocialDefault);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.RequestVerticalLibrary(VerticalVideoSelection.LegacySingle));
        job.RequestVerticalLibrary(VerticalVideoSelection.AllEligible);
        Assert.True(job.UsesVerticalLibrary);
        job.UseLegacyVertical();
        Assert.False(job.UsesVerticalLibrary);
        Assert.Null(job.VerticalSelection);
    }

    [Fact]
    public void EachVideoHasADeterministicKeyAndTheSamePlanCannotBeAddedTwice()
    {
        var jobId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var library = CampaignVideoLibrary.Create(Guid.NewGuid(), Guid.NewGuid(), jobId,
            VerticalVideoSelection.RecommendedThree, VerticalFormatRules.Version, null, Now);
        var plan = Recipes()[VerticalCreativeFormat.WowFeature].ToVariantPlan(1);
        var variant = library.AddVariant(plan, Now);

        Assert.Equal("11111111222233334444555555555555:WowFeature:1:wow-feature-v2:a", variant.IdempotencyKey);
        Assert.Equal(variant.IdempotencyKey,
            CampaignVideoVariant.CreateIdempotencyKey(jobId, VerticalCreativeFormat.WowFeature, 1, "wow-feature-v2", null));
        Assert.Throws<InvalidOperationException>(() => library.AddVariant(plan, Now));
        Assert.Throws<ArgumentException>(() =>
            CampaignVideoVariant.CreateIdempotencyKey(jobId, VerticalCreativeFormat.WowFeature, 1, "v1", "no spaces"));

        // A second hook variant of the same format is a different video.
        var hookB = library.AddVariant(plan with { Sequence = 2, HookVariant = "B" }, Now);
        Assert.EndsWith(":2:wow-feature-v2:b", hookB.IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFormatBuildsAGroundedVerticalPlanThatMatchesTheVideoSchema()
    {
        var master = Master();
        foreach (var (format, recipe) in Recipes())
        {
            var specification = VerticalVariantSpecificationBuilder.Build(master, recipe, Photos(), Videos());
            Assert.Equal(VideoAspectRatio.Vertical9By16, specification.AspectRatio);
            Assert.Equal((1_080, 1_920), (specification.Output.Width, specification.Output.Height));
            Assert.Equal(recipe.DurationSeconds, (int)specification.RequestedDuration);
            Assert.Equal(recipe.DurationSeconds * 1_000, specification.Scenes.Sum(scene => scene.DurationMs));
            Assert.Equal(recipe.CallToAction.Text, specification.CallToAction.Text);

            var keys = specification.FactBindings.Select(binding => binding.Key).ToHashSet(StringComparer.Ordinal);
            Assert.All(specification.Scenes.SelectMany(scene => scene.TextOverlays),
                overlay => Assert.Contains(overlay.GroundingKey, keys));
            Assert.All(specification.Audio.NarrationSegments, segment =>
            {
                Assert.Contains(segment.GroundingKey, keys);
                Assert.True(segment.StartMs + segment.DurationMs <= recipe.DurationSeconds * 1_000);
            });
            if (recipe.Narration == VerticalNarrationStructure.MusicOnly)
            {
                Assert.Empty(specification.Audio.NarrationSegments);
            }

            // Template copy is attributed to the format, never to the property or the agent.
            Assert.All(specification.FactBindings.Where(binding => binding.Key.StartsWith("template.", StringComparison.Ordinal)),
                binding => Assert.Equal(FactSource.BrandKit, binding.Source));
            Assert.All(specification.Scenes, scene => Assert.True(
                scene.VisualSource.PropertyMediaId is not null || scene.VisualSource.PropertyVideoId is not null));

            // Agent-confirmed facts are allowed in derived plans only (as with the price-drop pill); the canonical
            // schema covers what the AI director may plan, so check everything else against it.
            var agentKeys = recipe.Facts
                .Where(fact => fact.Source is VerticalCopySource.AgentClaim or VerticalCopySource.AgentCampaignInput
                    or VerticalCopySource.AgentStoryboard)
                .Select(fact => fact.Key)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(specification.FactBindings.Where(binding => binding.Source == FactSource.AgentVerifiedCampaignInput),
                binding => Assert.Contains(binding.Key, agentKeys));
            var canonical = specification with
            {
                FactBindings = specification.FactBindings
                    .Select(binding => binding.Source == FactSource.AgentVerifiedCampaignInput
                        ? binding with { Source = FactSource.VerifiedProperty }
                        : binding)
                    .ToArray(),
            };
            var json = JsonSerializer.SerializeToElement(canonical, VideoSpecificationJson.Options);
            Assert.True(VideoSchemaContract.Validate(json).Count == 0,
                $"{format}: {string.Join(" ", VideoSchemaContract.Validate(json))}");
        }
    }

    [Fact]
    public void FootageTooShortForItsBeatFallsBackToThePrimaryPhoto()
    {
        var recipe = Recipes()[VerticalCreativeFormat.HostedPov];
        var footageBeats = recipe.Beats.Where(beat => beat.PropertyVideoId is not null).ToArray();
        Assert.NotEmpty(footageBeats);
        var shortVideos = Videos().Select(video => video with { DurationMs = 500 }).ToArray();

        var specification = VerticalVariantSpecificationBuilder.Build(Master(), recipe, Photos(), shortVideos);

        Assert.DoesNotContain(specification.Scenes, scene => scene.VisualSource.Kind == VisualSourceKind.PropertyVideo);
    }

    [Fact]
    public void FormatsDecideTheVoiceAndCaptionsAndOnlyAConsentedHostGetsThePresenter()
    {
        var campaign = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = Guid.NewGuid(),
            AgentAvatarConsentConfirmed = true,
        };
        var recipes = Recipes();

        var wow = VerticalVariantSpecificationBuilder.OptionsFor(campaign, recipes[VerticalCreativeFormat.WowFeature]);
        Assert.Equal(CampaignVoiceoverMode.None, wow.Voiceover);
        Assert.False(wow.Elements.HasFlag(CampaignVideoElement.NarrationCaptions));
        Assert.False(wow.Elements.HasFlag(CampaignVideoElement.AgentAvatar));

        var hosted = recipes[VerticalCreativeFormat.HostedPov] with { UsesPresenter = true };
        var withHost = VerticalVariantSpecificationBuilder.OptionsFor(campaign, hosted);
        Assert.True(withHost.Elements.HasFlag(CampaignVideoElement.NarrationCaptions));
        Assert.True(withHost.Elements.HasFlag(CampaignVideoElement.AgentAvatar));

        var unconsented = VerticalVariantSpecificationBuilder.OptionsFor(campaign with { AgentAvatarConsentConfirmed = false }, hosted);
        Assert.False(unconsented.Elements.HasFlag(CampaignVideoElement.AgentAvatar));
    }

    [Fact]
    public void RecipeCopySourcesMapOntoTheStoredFactSourcesWithoutNewValues()
    {
        Assert.Equal(FactSource.VerifiedProperty, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.VerifiedProperty));
        Assert.Equal(FactSource.AcceptedScript, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.AgentNarration));
        Assert.Equal(FactSource.AgentVerifiedCampaignInput, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.AgentClaim));
        Assert.Equal(FactSource.AgentVerifiedCampaignInput, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.AgentStoryboard));
        Assert.Equal(FactSource.PropertyStory, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.MediaAnalysis));
        Assert.Equal(FactSource.BrandKit, VerticalVariantSpecificationBuilder.ToFactSource(VerticalCopySource.ApprovedTemplate));
        Assert.All(Enum.GetValues<VerticalCopySource>(), source =>
            Assert.True(Enum.IsDefined(VerticalVariantSpecificationBuilder.ToFactSource(source))));
    }

    [Fact]
    public void EachVideosPostCaptionUsesOnlyItsOwnHookFactsAndCallToAction()
    {
        var recipe = Recipes()[VerticalCreativeFormat.PriceFirst];
        var captions = VerticalVariantCaptions.From(recipe);
        var round = VerticalVariantCaptions.FromJson(captions.ToJson())!;

        Assert.Equal(recipe.Hook.Text, round.Hook);
        Assert.Equal(recipe.CallToAction.Text, round.CallToAction);
        Assert.StartsWith(recipe.Hook.Text, round.LongCaption, StringComparison.Ordinal);
        var allowed = recipe.Facts.Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(round.GroundingKeys, key => Assert.Contains(key, allowed));
        foreach (var line in round.LongCaption.Split('\n'))
        {
            Assert.Contains(recipe.Facts, fact => fact.Value == line);
        }
    }

    [Fact]
    public void LibraryVideosAreStoredByCampaignLibraryVideoAndVersionWithAPlainName()
    {
        var organization = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var property = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var job = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var library = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        var variant = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

        var path = CampaignGenerationProcessor.VariantAssetPath(
            organization, property, job, library, variant, VerticalCreativeFormat.WowFeature, 2);

        Assert.Equal(
            "organizations/aaaaaaaa000000000000000000000001/properties/bbbbbbbb000000000000000000000002"
            + "/campaigns/cccccccc000000000000000000000003/library/dddddddd000000000000000000000004"
            + "/variants/eeeeeeee000000000000000000000005/v2/bbbbbbbb-wow-feature.mp4",
            path);
        Assert.EndsWith("-price-first.mp4", CampaignGenerationProcessor.VariantAssetPath(
            organization, property, job, library, variant, VerticalCreativeFormat.PriceFirst, 1), StringComparison.Ordinal);
    }

    internal static Dictionary<VerticalCreativeFormat, VerticalCreativeRecipe> Recipes() =>
        VerticalCreativePlan.Create(RecipeFixture.Rich() with
        {
            AccessClaim = new VerticalAccessClaimInput(Guid.NewGuid(), "Private showings this weekend only"),
        }).Recipes.ToDictionary(recipe => recipe.Format);

    internal static VideoProductionSpecification Master()
    {
        using var stream = typeof(VerticalVariantGenerationTests).Assembly.GetManifestResourceStream("CanonicalVideoSample")!;
        return JsonSerializer.Deserialize<VideoProductionSpecification>(stream, VideoSpecificationJson.Options)!;
    }

    internal static VerticalVariantPhotoSize[] Photos() => RecipeFixture.Rich().Photos
        .Select(photo => new VerticalVariantPhotoSize(photo.MediaId, photo.Width, photo.Height))
        .ToArray();

    internal static VerticalVariantVideoSize[] Videos() => RecipeFixture.Rich().Footage
        .Select(video => new VerticalVariantVideoSize(video.VideoId, 1_920, 1_080, video.DurationMs))
        .ToArray();

    private static CampaignGenerationJob RunningJob(CampaignGenerationStage stage)
    {
        var job = CampaignGenerationJob.Create(Guid.NewGuid(), Guid.NewGuid(), "user", new string('a', 64),
            CampaignOutputSelection.TikTok, Now, CampaignCreativeOptions.SocialDefault);
        foreach (var next in Enum.GetValues<CampaignGenerationStage>()
            .Where(candidate => !CampaignGenerationStageOrder.IsVerticalLibraryStage(candidate))
            .OrderBy(CampaignGenerationStageOrder.Rank)
            .SkipWhile(candidate => candidate == CampaignGenerationStage.ValidateProperty)
            .TakeWhile(candidate => CampaignGenerationStageOrder.Rank(candidate) <= CampaignGenerationStageOrder.Rank(stage)))
        {
            Begin(job);
            job.CompleteStage(next, Now);
        }

        Begin(job);
        return job;
    }

    private static void Begin(CampaignGenerationJob job) => job.BeginStage(Now, TimeSpan.FromMinutes(5));
}
