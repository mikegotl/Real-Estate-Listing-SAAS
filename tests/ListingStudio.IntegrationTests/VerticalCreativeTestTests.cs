using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 9: the admin Creative Format Testing tab's suites, cases, worker and kill switch.</summary>
public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task CreativeHookTestMakesAndValidatesOneVideoPerCaseOutsideTheRegressionGallery()
    {
        var (admin, owner) = await CreateLibraryListingAsync("creative-hooks");
        await GrantReviewAdminAsync(owner.UserId);
        await using var factory = fixture.CreateFactory(_ => { });
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            QueueCreativeProviders(factory, campaigns: 2);
            Guid suiteId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>();
                Assert.True(await service.IsEnabledAsync(owner.UserId));
                var preview = (await service.PreviewAsync(owner.UserId, owner.PropertyId))!;
                Assert.Equal(VerticalFormatRules.Formats.Count, preview.Formats.Count);
                Assert.All(preview.Formats, option => Assert.False(string.IsNullOrWhiteSpace(option.RecipeVersion)));
                var suite = await service.StartAsync(owner.UserId, owner.PropertyId, VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.PriceFirst,
                    CampaignCaptionStyle.Comic);
                suiteId = suite.Id;
                Assert.Equal(CampaignCaptionStyle.Comic, suite.CaptionStyle);
                Assert.Equal(["a", "b"], suite.Cases.Select(item => item.HookVariant));
                Assert.All(suite.Cases, item => Assert.Equal(VerticalCreativeTestCaseStatus.Pending, item.Status));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.StartAsync(owner.UserId, owner.PropertyId, VerticalCreativeTestMode.SingleFormat, VerticalCreativeFormat.PriceFirst));
            }

            var validations = factory.RenderedVideoValidator.CallCount;
            var finished = await RunCreativeSuiteAsync(factory, owner, suiteId);
            Assert.Equal(VerticalCreativeTestSuiteStatus.Completed, finished.Status);
            Assert.All(finished.Cases, item =>
            {
                Assert.True(item.Status == VerticalCreativeTestCaseStatus.Passed, $"{item.HookVariant}: {item.Status} {item.Error}");
                Assert.NotNull(item.DeliverableId);
                Assert.Equal("price-first-v1", item.RecipeVersion);
                Assert.Equal(1, item.Version);
                Assert.Null(item.Decision);
            });
            Assert.NotEqual(finished.Cases[0].JobId, finished.Cases[1].JobId);
            await using (var styled = factory.Services.CreateAsyncScope())
            {
                // Every test campaign renders with the caption look picked for the test.
                var jobIds = finished.Cases.Select(item => item.JobId!.Value).ToArray();
                var styles = await styled.ServiceProvider.GetRequiredService<ListingStudio.Infrastructure.Persistence.ApplicationDbContext>()
                    .CampaignGenerationJobs.Where(job => jobIds.Contains(job.Id)).Select(job => job.CaptionStyle).ToListAsync();
                Assert.Equal([CampaignCaptionStyle.Comic, CampaignCaptionStyle.Comic], styles);
                Assert.Equal(CampaignCaptionStyle.Comic, factory.VideoRenderer.LastRequest!.CaptionStyle);
            }

            Assert.StartsWith("What ", finished.Cases[1].Hook, StringComparison.Ordinal);
            Assert.DoesNotContain("What ", finished.Cases[0].Hook, StringComparison.Ordinal);
            // Each case's own file got a full technical check after its campaign finished (in addition to the render's).
            Assert.True(factory.RenderedVideoValidator.CallCount >= validations + 2);

            await using var review = factory.Services.CreateAsyncScope();
            var reviews = review.ServiceProvider.GetRequiredService<ICampaignReviewService>();
            await reviews.DecideDeliverableAsync(owner.UserId, finished.Cases[0].DeliverableId!.Value, 1, CampaignVideoReviewDecision.Approved, null);
            await reviews.DecideDeliverableAsync(owner.UserId, finished.Cases[1].DeliverableId!.Value, 1,
                CampaignVideoReviewDecision.ChangesRequested, "Try a slower open");
            var listed = Assert.Single(await review.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().ListAsync(owner.UserId));
            Assert.Equal(CampaignVideoReviewDecision.Approved, listed.Cases[0].Decision);
            Assert.Equal(CampaignVideoReviewDecision.ChangesRequested, listed.Cases[1].Decision);
            var summary = VerticalCreativeTestSummary.From([listed]);
            Assert.Equal((0, 0, 1, 0), (summary.AwaitingReview, summary.TechnicalFailures, summary.ChangesRequested, summary.PublishedWithMetrics));

            // The render regression gallery never shows creative test campaigns.
            var gallery = await reviews.ListAsync(owner.UserId);
            Assert.DoesNotContain(gallery, item => listed.Cases.Any(test => test.JobId == item.JobId));

            // Kill switch: the tab's calls are refused, the evidence stays, and it all comes back with the switch.
            await SetLibrarySwitchAsync(admin, false, false);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await using (var off = factory.Services.CreateAsyncScope())
            {
                var service = off.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>();
                Assert.False(await service.IsEnabledAsync(owner.UserId));
                await Assert.ThrowsAsync<CreativeTestingDisabledException>(() => service.ListAsync(owner.UserId));
                await Assert.ThrowsAsync<CreativeTestingDisabledException>(() => service.PreviewAsync(owner.UserId, owner.PropertyId));
                await Assert.ThrowsAsync<CreativeTestingDisabledException>(() =>
                    service.StartAsync(owner.UserId, owner.PropertyId, VerticalCreativeTestMode.RecommendedThree, null));
                await Assert.ThrowsAsync<CreativeTestingDisabledException>(() => service.CancelAsync(owner.UserId, suiteId));
                Assert.Equal(2, await off.ServiceProvider.GetRequiredService<ApplicationDbContext>().VerticalCreativeTestCases
                    .CountAsync(item => item.SuiteId == suiteId && item.Status == VerticalCreativeTestCaseStatus.Passed));
            }

            await SetLibrarySwitchAsync(admin, true, true);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await using var on = factory.Services.CreateAsyncScope();
            Assert.Equal(2, Assert.Single(await on.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().ListAsync(owner.UserId))
                .Cases.Count(item => item.CanReview));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task KillSwitchStopsQueuedCreativeCasesFromStartingAndLeavesTheRegressionSuiteUsable()
    {
        var (admin, owner) = await CreateLibraryListingAsync("creative-switch");
        await GrantReviewAdminAsync(owner.UserId);
        await using var factory = fixture.CreateFactory(_ => { });
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            Guid suiteId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                suiteId = (await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().StartAsync(
                    owner.UserId, owner.PropertyId, VerticalCreativeTestMode.SingleFormat, VerticalCreativeFormat.PriceFirst)).Id;
            }

            // Global off beats the pilot list: the queued case never starts a campaign.
            await SetLibrarySwitchAsync(admin, false, false, owner.OrganizationId);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            for (var tick = 0; tick < 3; tick++)
            {
                await TickCreativeAsync(factory);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var waiting = await db.VerticalCreativeTestCases.AsNoTracking().SingleAsync(item => item.SuiteId == suiteId);
                Assert.Equal(VerticalCreativeTestCaseStatus.Pending, waiting.Status);
                Assert.Null(waiting.CampaignGenerationJobId);
                Assert.False(await db.CampaignGenerationJobs.AnyAsync(job => job.PropertyId == owner.PropertyId));

                // The render regression suite keeps working with the switch off.
                var regression = scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
                var started = await regression.StartAsync(owner.UserId, owner.PropertyId);
                Assert.Single(await regression.ListAsync(owner.UserId));
                await regression.CancelAsync(owner.UserId, started.Id);
            }

            // Switched back on, the same case starts and its campaign is a library campaign with exactly that format.
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await TickCreativeAsync(factory);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var running = await db.VerticalCreativeTestCases.AsNoTracking().SingleAsync(item => item.SuiteId == suiteId);
                Assert.Equal(VerticalCreativeTestCaseStatus.Running, running.Status);
                var job = await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == running.CampaignGenerationJobId);
                Assert.Equal(VerticalVideoSelection.AgentChosen, job.VerticalSelection);
                Assert.Equal([VerticalCreativeFormat.PriceFirst], job.VerticalFormats);
                Assert.Null(job.VerticalHookVariant);
                Assert.Equal(CampaignOutputSelection.TikTok, job.RequestedOutputs);
                await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().CancelAsync(owner.UserId, suiteId);
                Assert.Equal(VerticalCreativeTestSuiteStatus.Cancelled, (await db.VerticalCreativeTestSuites.AsNoTracking()
                    .SingleAsync(item => item.Id == suiteId)).Status);
            }
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task CreativeTestsNeedAPlatformAdminAndStayInTheirWorkspace()
    {
        var (admin, owner) = await CreateLibraryListingAsync("creative-auth");
        var (_, outsider) = await CreateLibraryListingAsync("creative-auth-outsider");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>();
            Assert.False(await service.IsEnabledAsync(owner.UserId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(owner.UserId, owner.PropertyId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.StartAsync(owner.UserId, owner.PropertyId, VerticalCreativeTestMode.RecommendedThree, null));

            await GrantReviewAdminAsync(owner.UserId);
            await GrantReviewAdminAsync(outsider.UserId);
            Assert.Null(await service.PreviewAsync(owner.UserId, outsider.PropertyId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.StartAsync(owner.UserId, outsider.PropertyId, VerticalCreativeTestMode.SingleFormat, VerticalCreativeFormat.PriceFirst));
            var suite = await service.StartAsync(owner.UserId, owner.PropertyId, VerticalCreativeTestMode.SingleFormat, VerticalCreativeFormat.PriceFirst);
            Assert.Empty(await service.ListAsync(outsider.UserId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelAsync(outsider.UserId, suite.Id));
            await service.CancelAsync(owner.UserId, suite.Id);
            var cancelled = Assert.Single(await service.ListAsync(owner.UserId));
            Assert.Equal(VerticalCreativeTestSuiteStatus.Cancelled, cancelled.Status);
            Assert.Equal(VerticalCreativeTestCaseStatus.Cancelled, Assert.Single(cancelled.Cases).Status);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    /// <summary>Photo analysis once, then story, plan, master voice and the Price First voice for each campaign.</summary>
    private static void QueueCreativeProviders(ListingStudioWebApplicationFactory factory, int campaigns)
    {
        foreach (var photo in LibraryPhotos)
        {
            factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
            {
                Category = photo.Category,
                RoomType = photo.Stop,
                HeroScore = photo.Hero,
                QualityScore = photo.Quality,
            });
        }

        for (var index = 0; index < campaigns; index++)
        {
            factory.StoryGenerator.Enqueue(SafeStory);
            factory.VideoDirector.Enqueue(CreateEditorialPlan);
            if (index == 0)
            {
                // Later campaigns on the same unchanged listing reuse the cached master narration.
                factory.VoiceProvider.Enqueue(CreateVoiceResult);
            }

            factory.CampaignLocalizationService.Enqueue(request => Same(request));
            factory.CampaignLocalizationService.Enqueue(request => Same(request));
            factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        }

        static CampaignLocalizedCopy Same(CampaignLocalizationRequest request)
        {
            var same = request.NarrationSegments.Select(segment => new CampaignLocalizedSegment(segment.Id, segment.Text)).ToArray();
            return new CampaignLocalizedCopy(same, same);
        }
    }

    private static async Task TickCreativeAsync(ListingStudioWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestProcessor>().ProcessNextAsync();
    }

    /// <summary>Alternates the creative worker and the campaign worker until the suite finishes.</summary>
    private static async Task<VerticalCreativeTestSuiteView> RunCreativeSuiteAsync(
        ListingStudioWebApplicationFactory factory, OwnerProperty owner, Guid suiteId)
    {
        VerticalCreativeTestSuiteView? suite = null;
        for (var index = 0; index < 150; index++)
        {
            await TickCreativeAsync(factory);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                suite = (await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().ListAsync(owner.UserId))
                    .Single(item => item.Id == suiteId);
                if (!suite.IsActive)
                {
                    return suite;
                }

                await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextStageAsync();
            }

            factory.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        }

        throw new Xunit.Sdk.XunitException($"The creative test did not finish: {string.Join("; ", suite!.Cases.Select(item => $"{item.HookVariant} {item.Status} {item.Error}"))}");
    }
}
