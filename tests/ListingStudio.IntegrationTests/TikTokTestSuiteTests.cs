using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashRecoveryReattachesAFailedOrCancelledCampaignInsteadOfSpendingAgain(bool cancel)
    {
        var owner = await CreateOwnerAndPropertyAsync("suite-crash-recovery");
        await GrantReviewAdminAsync(owner.UserId);
        await UploadAsync(owner);
        await using var factory = fixture.CreateFactory(_ => { });
        Guid jobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var suite = await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().StartAsync(owner.UserId, owner.PropertyId);
            var keyBytes = suite.Id.ToByteArray();
            keyBytes[15] ^= 1;
            // Simulate the crash window: campaign enqueue committed, suite association never saved.
            var job = await scope.ServiceProvider.GetRequiredService<ICampaignTestGenerationService>().EnqueueTestAsync(
                owner.UserId, owner.PropertyId, TikTokTestSuiteService.OptionsFor(0), new Guid(keyBytes));
            Assert.NotNull(job);
            jobId = job.Id;
            if (cancel)
                Assert.True(await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().CancelAsync(owner.UserId, jobId));
        }
        if (!cancel)
        {
            factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
            for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
                factory.StoryGenerator.Enqueue(new TimeoutException("test crash recovery timeout"));
            await ProcessSuiteCampaignAsync(factory, owner.UserId, jobId, CampaignGenerationStatus.Failed);
        }
        await TickTikTokSuiteAsync(factory); // Recover and associate the terminal job.
        await TickTikTokSuiteAsync(factory); // Stop from its persisted terminal state.
        await using var verification = factory.Services.CreateAsyncScope();
        var recovered = Assert.Single(await verification.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
        Assert.Equal(cancel ? TikTokTestSuiteStatus.Cancelled : TikTokTestSuiteStatus.Failed, recovered.Status);
        Assert.Equal(jobId, recovered.CaseJobIds[0]);
        Assert.Null(recovered.CaseJobIds[1]);
        Assert.Equal(1, await verification.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CampaignGenerationJobs.CountAsync(j => j.OrganizationId == owner.OrganizationId));
    }

    [Fact]
    public async Task TikTokSuiteRequiresAdminAndScopesStartReadAndCancelToWorkspace()
    {
        var owner = await CreateOwnerAndPropertyAsync("suite-owner");
        var outsider = await CreateOwnerAndPropertyAsync("suite-outsider");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(owner.UserId, owner.PropertyId));
        await GrantReviewAdminAsync(owner.UserId);
        await GrantReviewAdminAsync(outsider.UserId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.StartAsync(owner.UserId, outsider.PropertyId));
        var suite = await service.StartAsync(owner.UserId, owner.PropertyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(owner.UserId, owner.PropertyId));
        Assert.Empty(await service.ListAsync(outsider.UserId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelAsync(outsider.UserId, suite.Id));
        await service.CancelAsync(owner.UserId, suite.Id);
        Assert.Equal(TikTokTestSuiteStatus.Cancelled, Assert.Single(await service.ListAsync(owner.UserId)).Status);
    }

    [Fact]
    public async Task TikTokSuiteSequentiallyCreatesThreeFreshCampaignsAndKeepsTheirVideoIds()
    {
        var owner = await CreateOwnerAndPropertyAsync("suite-complete");
        await using var factory = fixture.CreateFactory(_ => { });
        await GrantReviewAdminAsync(owner.UserId);
        await BillingPlanTestData.SubscribeAsync(factory.Services, owner.UserId, SubscriptionPlan.Enterprise,
            factory.TimeProvider.GetUtcNow());
        await UploadAsync(owner);
        factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        for (var index = 0; index < 3; index++)
        {
            factory.StoryGenerator.Enqueue(SafeStory);
            factory.VideoDirector.Enqueue(CreateEditorialPlan);
        }
        for (var index = 0; index < 8; index++) factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);

        Guid suiteId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            suiteId = (await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>()
                .StartAsync(owner.UserId, owner.PropertyId)).Id;
        }
        var jobs = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            await TickTikTokSuiteAsync(factory);
            TikTokTestSuiteResult suite;
            await using (var read = factory.Services.CreateAsyncScope())
                suite = Assert.Single(await read.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
            Assert.Equal(suiteId, suite.Id);
            Assert.Equal(index, suite.CurrentCaseIndex);
            var jobId = suite.CaseJobIds[index];
            Assert.NotNull(jobId);
            jobs.Add(jobId.Value);
            var completed = await ProcessSuiteCampaignAsync(factory, owner.UserId, jobId.Value, CampaignGenerationStatus.Completed);
            Assert.Equal(CampaignOutputSelection.TikTok, completed.RequestedOutputs);
            Assert.Equal(index == 2 ? CampaignVoiceoverMode.Full : index == 1 ? CampaignVoiceoverMode.None : CampaignVoiceoverMode.ShortHook,
                completed.CreativeOptions.Voiceover);
            Assert.Equal(index == 2 ? 60 : 30, Assert.Single(completed.Deliverables).DurationSeconds);
            Assert.False(completed.CreativeOptions.Elements.HasFlag(CampaignVideoElement.AgentAvatar));
            await TickTikTokSuiteAsync(factory);
        }
        Assert.Equal(3, jobs.Distinct().Count());
        await using var verification = factory.Services.CreateAsyncScope();
        var finished = Assert.Single(await verification.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
        Assert.Equal(TikTokTestSuiteStatus.Completed, finished.Status);
        Assert.Equal(3, finished.CurrentCaseIndex);
        var gallery = await verification.ServiceProvider.GetRequiredService<ICampaignReviewService>().ListAsync(owner.UserId);
        Assert.Equal(3, gallery.Count);
        Assert.All(gallery, item => Assert.Null(item.Decision));
    }

    [Fact]
    public async Task AFailedTestCampaignStopsTheSuiteAndKeepsTheFailedJobForReview()
    {
        var owner = await CreateOwnerAndPropertyAsync("suite-failed-campaign");
        await GrantReviewAdminAsync(owner.UserId);
        await UploadAsync(owner);
        await using var factory = fixture.CreateFactory(_ => { });
        factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
            factory.StoryGenerator.Enqueue(new TimeoutException("test provider timeout"));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().StartAsync(owner.UserId, owner.PropertyId);
        await TickTikTokSuiteAsync(factory);
        Guid jobId;
        await using (var read = factory.Services.CreateAsyncScope())
        {
            var suite = Assert.Single(await read.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
            Assert.NotNull(suite.CaseJobIds[0]);
            jobId = suite.CaseJobIds[0]!.Value;
        }
        await ProcessSuiteCampaignAsync(factory, owner.UserId, jobId, CampaignGenerationStatus.Failed);
        await TickTikTokSuiteAsync(factory);
        await using var verification = factory.Services.CreateAsyncScope();
        var stopped = Assert.Single(await verification.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
        Assert.Equal(TikTokTestSuiteStatus.Failed, stopped.Status);
        Assert.Equal(jobId, stopped.CaseJobIds[0]);
        Assert.Null(stopped.CaseJobIds[1]);
        Assert.Null(stopped.CaseJobIds[2]);
        Assert.NotNull(stopped.Error);
    }

    [Fact]
    public async Task RevokingAdminPreventsQueuedSuiteFromStartingAPaidCampaign()
    {
        var owner = await CreateOwnerAndPropertyAsync("suite-role-revoked");
        await GrantReviewAdminAsync(owner.UserId);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().StartAsync(owner.UserId, owner.PropertyId);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(owner.UserId);
            Assert.NotNull(user);
            Assert.True((await users.RemoveFromRoleAsync(user, "PlatformAdmin")).Succeeded);
        }
        await TickTikTokSuiteAsync();
        await GrantReviewAdminAsync(owner.UserId);
        await using var read = fixture.Factory.Services.CreateAsyncScope();
        var stopped = Assert.Single(await read.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
        Assert.Equal(TikTokTestSuiteStatus.Failed, stopped.Status);
        Assert.All(stopped.CaseJobIds, id => Assert.Null(id));
    }

    [Fact]
    public async Task SessionArchiveIsPersistentReversibleAndScopedAndActiveSessionsMustBeCancelled()
    {
        var owner = await CreateOwnerAndPropertyAsync("archive-session-owner");
        var outsider = await CreateOwnerAndPropertyAsync("archive-session-outsider");
        await GrantReviewAdminAsync(owner.UserId);
        await GrantReviewAdminAsync(outsider.UserId);
        Guid sessionId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
            sessionId = (await service.StartAsync(owner.UserId, owner.PropertyId)).Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArchivedAsync(owner.UserId, sessionId, true));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetArchivedAsync(outsider.UserId, sessionId, true));
            await service.CancelAsync(owner.UserId, sessionId);
            await service.SetArchivedAsync(owner.UserId, sessionId, true);
        }
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
            var archived = Assert.Single(await service.ListAsync(owner.UserId));
            Assert.NotNull(archived.ArchivedAtUtc);
            Assert.Equal(TikTokTestSuiteStatus.Cancelled, archived.Status);
            Assert.Empty(await service.ListAsync(outsider.UserId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => scope.ServiceProvider.GetRequiredService<ICampaignReviewService>()
                .ListSessionAsync(outsider.UserId, sessionId));
            await service.SetArchivedAsync(owner.UserId, sessionId, false);
        }
        await using var read = fixture.Factory.Services.CreateAsyncScope();
        var restored = Assert.Single(await read.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().ListAsync(owner.UserId));
        Assert.Null(restored.ArchivedAtUtc);
        Assert.Equal(sessionId, restored.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingASessionCancelsItsCampaignAndDoesNotEnqueueMoreCases(bool running)
    {
        var owner = await CreateOwnerAndPropertyAsync("cancel-whole-session");
        await GrantReviewAdminAsync(owner.UserId);
        await UploadAsync(owner);
        await using var factory = fixture.CreateFactory(_ => { });
        Guid sessionId;
        await using (var scope = factory.Services.CreateAsyncScope())
            sessionId = (await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().StartAsync(owner.UserId, owner.PropertyId)).Id;
        await TickTikTokSuiteAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            if (running)
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var job = await db.CampaignGenerationJobs.SingleAsync(j => j.OrganizationId == owner.OrganizationId);
                job.BeginStage(factory.TimeProvider.GetUtcNow(), TimeSpan.FromMinutes(5));
                await db.SaveChangesAsync();
            }
            await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>().CancelAsync(owner.UserId, sessionId);
        }
        await TickTikTokSuiteAsync(factory);
        await using var read = factory.Services.CreateAsyncScope();
        var service = read.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
        var cancelled = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(TikTokTestSuiteStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CaseJobIds[0]);
        Assert.Null(cancelled.CaseJobIds[1]);
        var job = await read.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
            .GetAsync(owner.UserId, cancelled.CaseJobIds[0]!.Value);
        Assert.NotNull(job);
        Assert.True(job.CancellationRequested);
        Assert.Equal(running ? CampaignGenerationStatus.Running : CampaignGenerationStatus.Cancelled, job.Status);
        await service.SetArchivedAsync(owner.UserId, sessionId, true);
        Assert.Equal(cancelled.CaseJobIds.ToArray(), Assert.Single(await service.ListAsync(owner.UserId)).CaseJobIds.ToArray());
        Assert.Single(await read.ServiceProvider.GetRequiredService<ICampaignReviewService>().ListSessionAsync(owner.UserId, sessionId));
    }

    private async Task TickTikTokSuiteAsync(ListingStudioWebApplicationFactory? factory = null)
    {
        await using var scope = (factory ?? fixture.Factory).Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteProcessor>().ProcessNextAsync();
    }
    private static async Task<CampaignGenerationResult> ProcessSuiteCampaignAsync(
        ListingStudioWebApplicationFactory factory, string userId, Guid jobId, CampaignGenerationStatus expected)
    {
        for (var index = 0; index < 60; index++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextStageAsync();
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(10));
            var job = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().GetAsync(userId, jobId);
            Assert.NotNull(job);
            if (job.Status == expected) return job;
        }
        throw new InvalidOperationException("The suite campaign did not reach the expected state.");
    }

}
