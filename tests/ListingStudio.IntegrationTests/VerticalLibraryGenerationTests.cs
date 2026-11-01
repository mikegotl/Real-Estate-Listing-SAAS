using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 6: several recommended vertical videos from one campaign, behind VerticalVideoLibraryV1.</summary>
public sealed partial class CampaignGenerationTests
{
    private static readonly (string Stop, PropertyMediaCategory Category, int Hero, int Quality)[] LibraryPhotos =
    [
        (WalkthroughStoryboard.ExteriorFront, PropertyMediaCategory.FrontExterior, 66, 78),
        ("Kitchen", PropertyMediaCategory.Kitchen, 90, 86),
        ("Living room", PropertyMediaCategory.LivingRoom, 74, 80),
        ("Primary bedroom", PropertyMediaCategory.PrimaryBedroom, 52, 72),
        ("Back patio", PropertyMediaCategory.Patio, 63, 70),
    ];

    [Fact]
    public async Task LibraryOnRendersEachRecommendedFormatAsItsOwnVideoAndBillsTheCampaignOnce()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-on");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders();
            var voiceCalls = fixture.Factory.VoiceProvider.CallCount;
            var renders = fixture.Factory.VideoRenderer.CallCount;

            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(CampaignVideoLibraryStatus.Completed, library.Status);
            Assert.Equal(VerticalVideoSelection.RecommendedThree, library.Selection);
            Assert.Equal(VerticalFormatRules.Version, library.RuleVersion);
            Assert.Equal(
                [VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst],
                library.Variants.Select(item => item.Format).Order());
            Assert.All(library.Variants, variant =>
            {
                Assert.Equal(CampaignVideoVariantStatus.Rendered, variant.Status);
                Assert.Equal(
                    CampaignVideoVariant.CreateIdempotencyKey(queued.Id, variant.Format, variant.Sequence, variant.RecipeVersion, "a"),
                    variant.IdempotencyKey);
                var captions = VerticalVariantCaptions.FromJson(variant.CaptionsJson);
                Assert.NotNull(captions);
                Assert.Equal(variant.HookText, captions.Hook);
            });

            // One 9:16 deliverable per video, each stored under its own campaign/library/video/version path.
            var deliverables = await db.CampaignDeliverables.AsNoTracking()
                .Where(item => item.CampaignGenerationJobId == queued.Id)
                .ToListAsync();
            Assert.DoesNotContain(deliverables, item => item.CampaignVideoVariantId == null && item.Kind == CampaignOutputKind.TikTok);
            var storage = scope.ServiceProvider.GetRequiredService<ICampaignAssetStorage>();
            foreach (var variant in library.Variants)
            {
                var deliverable = Assert.Single(deliverables, item => item.CampaignVideoVariantId == variant.Id);
                Assert.Equal(CampaignDeliverableStatus.Rendered, deliverable.Status);
                Assert.Equal(Domain.Videos.VideoAspectRatio.Vertical9By16, deliverable.AspectRatio);
                Assert.Equal(variant.DurationSeconds, (int)deliverable.RequestedDuration);
                Assert.Equal(
                    CampaignGenerationProcessor.VariantAssetPath(owner.OrganizationId, owner.PropertyId, queued.Id,
                        library.Id, variant.Id, variant.Format, 1),
                    deliverable.AssetPath);
                await using var stored = await storage.OpenReadAsync(deliverable.AssetPath!);
                Assert.NotNull(stored);
            }

            // Wow Feature is music only and reuses nothing it does not speak; Price First has its own voiceover.
            var job = await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
            var wow = deliverables.Single(item => item.CampaignVideoVariantId == library.Variants
                .Single(variant => variant.Format == VerticalCreativeFormat.WowFeature).Id);
            var price = deliverables.Single(item => item.CampaignVideoVariantId == library.Variants
                .Single(variant => variant.Format == VerticalCreativeFormat.PriceFirst).Id);
            Assert.Equal(job.VideoNarrationId, wow.VideoNarrationId);
            Assert.Empty(Specification(wow).Audio.NarrationSegments);
            Assert.NotEqual(job.VideoNarrationId, price.VideoNarrationId);
            var spoken = Specification(price).Audio.NarrationSegments;
            Assert.NotEmpty(spoken);
            Assert.All(spoken, segment => Assert.Equal(segment.Text.ToUpperInvariant(), segment.Text));
            Assert.NotNull(library.Variants.Single(variant => variant.Format == VerticalCreativeFormat.PriceFirst)
                .NarrationLocalizationJson);
            Assert.Equal(voiceCalls + 2, fixture.Factory.VoiceProvider.CallCount);
            Assert.Equal(renders + 2, fixture.Factory.VideoRenderer.CallCount);

            // One accepted campaign, one usage record, however many videos it made.
            Assert.Equal(1, await db.CampaignUsageRecords.CountAsync(item => item.CampaignGenerationJobId == queued.Id));
            Assert.Empty(completed.Deliverables);

            // Asking again is the same campaign: no new library, videos, voiceovers or charge.
            var again = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            Assert.Equal(queued.Id, again.Id);
            Assert.Equal(1, await db.CampaignVideoLibraries.CountAsync(item => item.PropertyId == owner.PropertyId));
            Assert.Equal(deliverables.Count, await db.CampaignDeliverables.CountAsync(item => item.CampaignGenerationJobId == queued.Id));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task AllEligibleSelectionMakesEveryFormatTheListingCanSupport()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-all");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            QueueLibraryProviders();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.AllEligible);
            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var recommendation = await scope.ServiceProvider.GetRequiredService<IVerticalFormatRecommendationService>()
                .RecommendForOrganizationAsync(owner.OrganizationId, owner.PropertyId,
                    VerticalRecommendationContext.From(CampaignCreativeOptions.SocialDefault));
            var library = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignVideoLibraries
                .AsNoTracking().Include(item => item.Variants).SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(VerticalVideoSelection.AllEligible, library.Selection);
            Assert.Equal(
                recommendation!.AllEligible.Select(item => item.Format),
                library.Variants.OrderBy(item => item.Sequence).Select(item => item.Format));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task FailedVideoIsMarkedFailedWithoutTouchingTheVideoThatRendered()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-partial");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await ProcessUntilStageAsync(owner, queued.Id, CampaignGenerationStage.RenderVariants);

            // The first video fails its technical check on every attempt the stage allows.
            for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
            {
                fixture.Factory.RenderedVideoValidator.EnqueueFailure("The video stream could not be decoded.");
            }

            var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(CampaignVideoLibraryStatus.PartiallyCompleted, library.Status);
            var failed = Assert.Single(library.Variants, item => item.Status == CampaignVideoVariantStatus.Failed);
            var rendered = Assert.Single(library.Variants, item => item.Status == CampaignVideoVariantStatus.Rendered);
            Assert.Equal(1, failed.Sequence);
            Assert.False(string.IsNullOrWhiteSpace(failed.Error));
            var deliverables = await db.CampaignDeliverables.AsNoTracking()
                .Where(item => item.CampaignGenerationJobId == queued.Id).ToListAsync();
            Assert.Equal(CampaignDeliverableStatus.Planned, deliverables.Single(item => item.CampaignVideoVariantId == failed.Id).Status);
            Assert.Equal(CampaignDeliverableStatus.Rendered, deliverables.Single(item => item.CampaignVideoVariantId == rendered.Id).Status);
            Assert.Equal(CampaignGenerationStatus.Completed, completed.Status);
            Assert.Equal(1, await db.CampaignUsageRecords.CountAsync(item => item.CampaignGenerationJobId == queued.Id));

            // Prompt 8: the partial library shows the failed video as failed and downloads only the one that rendered.
            var libraryService = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
            var view = (await libraryService.GetAsync(owner.UserId, queued.Id))!;
            Assert.Equal(CampaignVideoTechnicalStatus.Failed, view.Videos.Single(item => item.VariantId == failed.Id).TechnicalStatus);
            Assert.False(string.IsNullOrWhiteSpace(view.Videos.Single(item => item.VariantId == failed.Id).Problem));
            Assert.Equal(CampaignVideoTechnicalStatus.Passed, view.Videos.Single(item => item.VariantId == rendered.Id).TechnicalStatus);
            var archive = (await libraryService.OpenArchiveAsync(owner.UserId, queued.Id))!;
            using var zipped = new MemoryStream();
            await archive.WriteAsync(zipped, CancellationToken.None);
            zipped.Position = 0;
            using var zip = new System.IO.Compression.ZipArchive(zipped);
            Assert.Single(zip.Entries, entry => entry.Name.EndsWith(".mp4", StringComparison.Ordinal));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task LibrarySwitchedOffMeansTheLegacyCampaignAndTheGlobalSwitchBeatsThePilotList()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-off");
        try
        {
            // Off everywhere: the library request is the legacy campaign, with the legacy fingerprint.
            await SetLibrarySwitchAsync(admin, false, false);
            QueueLibraryProviders(narratedVideos: 0);
            fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
            AssertPlatformCut(Assert.Single(completed.Deliverables), CampaignOutputKind.TikTok, 30, "9:16");

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Null((await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued.Id)).VerticalSelection);
                Assert.False(await db.CampaignVideoLibraries.AnyAsync(item => item.CampaignGenerationJobId == queued.Id));
                var legacy = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                    owner.UserId, owner.PropertyId, CampaignOutputSelection.TikTok,
                    CampaignCreativeOptions.SocialDefault, CampaignNarrationPreferences.Default);
                Assert.Equal(queued.Id, legacy!.Id);
            }

            // The kill switch wins over a pilot list that names the workspace.
            var pilot = await CreateOwnerAndPropertyAsync("library-killed");
            await UploadAsync(pilot);
            await SetLibrarySwitchAsync(admin, false, false, pilot.OrganizationId);
            var killed = await EnqueueLibraryAsync(pilot, VerticalVideoSelection.RecommendedThree);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Null((await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == killed.Id)).VerticalSelection);
                await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().CancelAsync(pilot.UserId, killed.Id);
            }
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task LibrarySwitchedOffWhileQueuedRunsTheLegacyCampaign()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-queued-off");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders(narratedVideos: 0);
            fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await SetLibrarySwitchAsync(admin, false, false, owner.OrganizationId);

            var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            AssertPlatformCut(Assert.Single(completed.Deliverables), CampaignOutputKind.TikTok, 30, "9:16");
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.CampaignVideoLibraries.AnyAsync(item => item.CampaignGenerationJobId == queued.Id));
            Assert.Null((await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued.Id)).VerticalSelection);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task LibrarySwitchedOffBetweenVideosKeepsTheFinishedOneStartsNoOtherAndReEnablingAddsNothing()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-between");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await ProcessUntilAsync(owner, queued.Id, () => RenderedVariantsAsync(queued.Id), count => count == 1);
            var renders = fixture.Factory.VideoRenderer.CallCount;

            await SetLibrarySwitchAsync(admin, false, false, owner.OrganizationId);
            var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            Assert.Equal(renders, fixture.Factory.VideoRenderer.CallCount);
            Assert.Empty(completed.Deliverables);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(CampaignVideoLibraryStatus.Cancelled, library.Status);
            Assert.Single(library.Variants, item => item.Status == CampaignVideoVariantStatus.Rendered);
            Assert.Single(library.Variants, item => item.Status == CampaignVideoVariantStatus.Cancelled);
            var deliverableCount = await db.CampaignDeliverables.CountAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(1, await db.CampaignUsageRecords.CountAsync(item => item.CampaignGenerationJobId == queued.Id));

            // Turning it back on does not restart or duplicate anything for the finished campaign.
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            var again = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            Assert.Equal(queued.Id, again.Id);
            await using var processScope = fixture.Factory.Services.CreateAsyncScope();
            await processScope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextStageAsync();
            Assert.Equal(deliverableCount, await db.CampaignDeliverables.CountAsync(item => item.CampaignGenerationJobId == queued.Id));
            Assert.Equal(1, await db.CampaignUsageRecords.CountAsync(item => item.CampaignGenerationJobId == queued.Id));
            Assert.Equal(renders, fixture.Factory.VideoRenderer.CallCount);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task InterruptedVideoResumesWithoutADuplicatePlanVoiceoverOrRender()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-resume");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders();
            var voiceCalls = fixture.Factory.VoiceProvider.CallCount;
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await ProcessUntilStageAsync(owner, queued.Id, CampaignGenerationStage.GenerateVariantNarration);

            // A worker stopped right after marking the first video as started, before its plan was saved.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var library = await db.CampaignVideoLibraries.Include(item => item.Variants)
                    .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
                var now = fixture.Factory.TimeProvider.GetUtcNow();
                library.MarkGenerating(now);
                library.Variants.Single(item => item.Sequence == 1).MarkGenerating(now);
                await db.SaveChangesAsync();
            }

            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            await using var readScope = fixture.Factory.Services.CreateAsyncScope();
            var read = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var variants = await read.CampaignVideoVariants.AsNoTracking()
                .Where(item => item.Library.CampaignGenerationJobId == queued.Id).ToListAsync();
            Assert.All(variants, item => Assert.Equal(CampaignVideoVariantStatus.Rendered, item.Status));
            var perVariant = await read.CampaignDeliverables.AsNoTracking()
                .Where(item => item.CampaignGenerationJobId == queued.Id && item.CampaignVideoVariantId != null)
                .GroupBy(item => item.CampaignVideoVariantId)
                .Select(group => group.Count())
                .ToListAsync();
            Assert.Equal(variants.Count, perVariant.Count);
            Assert.All(perVariant, count => Assert.Equal(1, count));
            Assert.Equal(voiceCalls + 2, fixture.Factory.VoiceProvider.CallCount);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    private static Domain.Videos.VideoProductionSpecification Specification(CampaignDeliverable deliverable) =>
        System.Text.Json.JsonSerializer.Deserialize<Domain.Videos.VideoProductionSpecification>(
            deliverable.SpecificationJson, ListingStudio.Application.Videos.VideoSpecificationJson.Options)!;

    private async Task<(string Admin, OwnerProperty Owner)> CreateLibraryListingAsync(string prefix)
    {
        var admin = await CreateOwnerAndPropertyAsync($"{prefix}-admin");
        await GrantReviewAdminAsync(admin.UserId);
        var owner = await CreateOwnerAndPropertyAsync(prefix);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        foreach (var photo in LibraryPhotos)
        {
            await using var content = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId,
                new PropertyMediaUpload($"{photo.Category}.png", "image/png", content.Length, content, photo.Stop));
        }

        return (admin.UserId, owner);
    }

    /// <summary>Analysis for the five photos, story, plan, master voice, and one voice per narrated library video.</summary>
    private void QueueLibraryProviders(int narratedVideos = 1)
    {
        foreach (var photo in LibraryPhotos)
        {
            fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
            {
                Category = photo.Category,
                RoomType = photo.Stop,
                HeroScore = photo.Hero,
                QualityScore = photo.Quality,
            });
        }

        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        if (narratedVideos > 0)
        {
            // The master voiceover keeps its own words; the Price First voiceover is "translated" to capitals.
            fixture.Factory.CampaignLocalizationService.Enqueue(request => Identity(request));
            for (var index = 0; index < narratedVideos; index++)
            {
                fixture.Factory.CampaignLocalizationService.Enqueue(request =>
                {
                    var upper = request.NarrationSegments
                        .Select(segment => new CampaignLocalizedSegment(segment.Id, segment.Text.ToUpperInvariant()))
                        .ToArray();
                    return new CampaignLocalizedCopy(upper, upper);
                });
                fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
            }
        }

        static CampaignLocalizedCopy Identity(CampaignLocalizationRequest request)
        {
            var same = request.NarrationSegments.Select(segment => new CampaignLocalizedSegment(segment.Id, segment.Text)).ToArray();
            return new CampaignLocalizedCopy(same, same);
        }
    }

    private async Task<CampaignGenerationResult> EnqueueLibraryAsync(OwnerProperty owner, VerticalVideoSelection selection)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
            owner.UserId, owner.PropertyId, CampaignOutputSelection.TikTok, CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default, selection))!;
    }

    private async Task SetLibrarySwitchAsync(string adminUserId, bool global, bool everyone, params Guid[] pilots)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFeatureStateService>()
            .ConfigureAsync(adminUserId, FeatureFlagKeys.VerticalVideoLibraryV1, global, everyone, pilots);
    }

    private async Task<int> RenderedVariantsAsync(Guid jobId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignVideoVariants
            .CountAsync(item => item.Library.CampaignGenerationJobId == jobId && item.Status == CampaignVideoVariantStatus.Rendered);
    }

    private Task ProcessUntilStageAsync(OwnerProperty owner, Guid jobId, CampaignGenerationStage stage) =>
        ProcessUntilAsync(owner, jobId, async () =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().GetAsync(owner.UserId, jobId))!
                .CurrentStage;
        }, current => current == stage);

    /// <summary>Runs one stage at a time until the condition holds.</summary>
    private async Task ProcessUntilAsync<T>(OwnerProperty owner, Guid jobId, Func<Task<T>> read, Func<T, bool> done)
    {
        for (var index = 0; index < 60; index++)
        {
            if (done(await read()))
            {
                return;
            }

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextStageAsync();
            }

            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        }

        await using var final = fixture.Factory.Services.CreateAsyncScope();
        var latest = await final.ServiceProvider.GetRequiredService<ICampaignGenerationService>().GetAsync(owner.UserId, jobId);
        throw new Xunit.Sdk.XunitException(
            $"Campaign {jobId} did not reach the condition; latest state was {latest?.Status} at {latest?.CurrentStage}: {latest?.LastError}");
    }
}
