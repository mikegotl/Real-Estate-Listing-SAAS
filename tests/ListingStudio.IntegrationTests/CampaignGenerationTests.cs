using ListingStudio.Application.Audio;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class CampaignGenerationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task UploadToGenerateWaitAndDownloadIsDurableIdempotentAndTenantScoped()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-owner");
        var outsider = await CreateOwnerAndPropertyAsync("campaign-outsider");
        await UploadAsync(owner);
        var storyCallsBefore = fixture.Factory.StoryGenerator.CallCount;
        var directorCallsBefore = fixture.Factory.VideoDirector.CallCount;
        var voiceCallsBefore = fixture.Factory.VoiceProvider.CallCount;
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        QueueSuccessfulProviders();

        var enqueues = await Task.WhenAll(EnqueueAsync(owner), EnqueueAsync(owner));
        var queued = enqueues[0];
        Assert.Equal(queued.Id, enqueues[1].Id);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            Assert.Null(await campaigns.GetAsync(outsider.UserId, queued.Id));
            Assert.Null(await campaigns.GetLatestAsync(outsider.UserId, owner.PropertyId));
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(100, completed.ProgressPercent);
        Assert.All(completed.Stages, stage => Assert.Equal(CampaignStageStatus.Completed, stage.Status));
        Assert.Equal(3, completed.Deliverables.Count);
        Assert.All(completed.Deliverables, deliverable =>
            Assert.Equal(CampaignDeliverableStatus.Rendered, deliverable.Status));
        Assert.Equal(SafeStory.SocialCaptionLong, completed.SocialCaptionLong);
        Assert.Equal(SafeStory.SocialCaptionShort, completed.SocialCaptionShort);
        Assert.Equal(storyCallsBefore + 1, fixture.Factory.StoryGenerator.CallCount);
        Assert.Equal(directorCallsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 1, fixture.Factory.VoiceProvider.CallCount);
        Assert.Equal(renderCallsBefore + 3, fixture.Factory.VideoRenderer.CallCount);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            foreach (var kind in Enum.GetValues<CampaignOutputKind>())
            {
                var download = await campaigns.OpenDeliverableAsync(owner.UserId, queued.Id, kind);
                Assert.NotNull(download);
                await using var content = download.Content;
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer);
                Assert.Equal([0, 1, 2, 3, 4, 5], buffer.ToArray());
                Assert.Equal("video/mp4", download.ContentType);
                Assert.Null(await campaigns.OpenDeliverableAsync(outsider.UserId, queued.Id, kind));
            }

            var reused = (await campaigns.EnqueueAsync(owner.UserId, owner.PropertyId))!;
            Assert.Equal(queued.Id, reused.Id);
            Assert.Equal(CampaignGenerationStatus.Completed, reused.Status);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await dbContext.CampaignGenerationJobs
                .AsNoTracking()
                .Include(job => job.Deliverables)
                .SingleAsync(job => job.Id == queued.Id);
            Assert.Equal(owner.OrganizationId, persisted.OrganizationId);
            Assert.All(persisted.Deliverables, item => Assert.Equal(owner.OrganizationId, item.OrganizationId));
            Assert.Equal(3, persisted.Deliverables.Count);
        }
    }

    [Fact]
    public async Task FailedRenderRetriesOnlyItsCheckpointAndCanBeManuallyResumed()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-retry");
        await UploadAsync(owner);
        var storyCallsBefore = fixture.Factory.StoryGenerator.CallCount;
        var directorCallsBefore = fixture.Factory.VideoDirector.CallCount;
        var voiceCallsBefore = fixture.Factory.VoiceProvider.CallCount;
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        QueueSuccessfulProviders();
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("first fake failure"));
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("second fake failure"));
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("third fake failure"));

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.RenderHero, failed.CurrentStage);
        Assert.Contains("rendered", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(storyCallsBefore + 1, fixture.Factory.StoryGenerator.CallCount);
        Assert.Equal(directorCallsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 1, fixture.Factory.VoiceProvider.CallCount);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            Assert.True(await campaigns.RetryAsync(owner.UserId, queued.Id));
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        Assert.Equal(CampaignGenerationStatus.Completed, completed.Status);
        Assert.Equal(storyCallsBefore + 1, fixture.Factory.StoryGenerator.CallCount);
        Assert.Equal(directorCallsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 1, fixture.Factory.VoiceProvider.CallCount);
        Assert.Equal(renderCallsBefore + 6, fixture.Factory.VideoRenderer.CallCount);
    }

    [Fact]
    public async Task QueuedCampaignCanBeCancelledAndAReplacementCanBeEnqueued()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-cancel");
        await UploadAsync(owner);
        var queued = await EnqueueAsync(owner);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        Assert.True(await campaigns.CancelAsync(owner.UserId, queued.Id));
        var cancelled = await campaigns.GetAsync(owner.UserId, queued.Id);
        Assert.Equal(CampaignGenerationStatus.Cancelled, cancelled!.Status);
        var replacement = await campaigns.EnqueueAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(replacement);
        Assert.NotEqual(queued.Id, replacement.Id);
        Assert.Equal(CampaignGenerationStatus.Queued, replacement.Status);
        Assert.True(await campaigns.CancelAsync(owner.UserId, replacement.Id));
    }

    private async Task<CampaignGenerationResult> ProcessUntilAsync(
        OwnerProperty owner,
        Guid jobId,
        CampaignGenerationStatus expected)
    {
        for (var index = 0; index < 60; index++)
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>()
                    .ProcessNextStageAsync();
            }

            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(10));
            await using var readScope = fixture.Factory.Services.CreateAsyncScope();
            var result = await readScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                .GetAsync(owner.UserId, jobId);
            Assert.NotNull(result);
            if (result.Status == expected)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"Campaign {jobId} did not reach {expected}.");
    }

    private async Task<CampaignGenerationResult> EnqueueAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
            .EnqueueAsync(owner.UserId, owner.PropertyId))!;
    }

    private void QueueSuccessfulProviders()
    {
        fixture.Factory.MediaAnalyzer.Enqueue(new PropertyMediaAnalysis(
            PropertyMediaCategory.FrontExterior,
            "Exterior",
            91,
            96,
            true,
            false,
            false,
            [],
            "Front exterior of a detached home.",
            0));
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
    }

    private static DirectedEditorialPlan CreateEditorialPlan(VideoDirectionRequest request)
    {
        var narrationBinding = request.FactBindings.Single(binding => binding.Key == "story.voiceover");
        var media = request.Media[0];
        var viewportHeight = 9m * media.Width / (16m * media.Height);
        var viewport = viewportHeight <= 1
            ? new NormalizedRect(0, 0, 1, viewportHeight)
            : new NormalizedRect(0, 0, 16m * media.Height / (9m * media.Width), 1);
        var narration = new NarrationSegment(
            "narration-1",
            500,
            3_000,
            narrationBinding.Value,
            narrationBinding.Key);
        return new DirectedEditorialPlan(
            new AudioPlan(
                [narration],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    30_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, media.MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [],
                    [],
                    [narration.Id]),
                new VideoScene(
                    2,
                    30_000,
                    30_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, media.MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Crossfade, 500),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [new TextOverlay(
                        "closing-cta",
                        request.CallToAction.Text,
                        request.CallToAction.GroundingKey,
                        25_000,
                        4_000,
                        OverlayAnchor.BottomCenter,
                        new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                        TextOverlayStyle.ClosingCta)],
                    [],
                    []),
            ]);
    }

    private static VoiceGenerationResult CreateVoiceResult(VoiceGenerationRequest request)
    {
        var segment = Assert.Single(request.Segments);
        return new VoiceGenerationResult(
            [1, 2, 3, 4],
            "audio/mpeg",
            ".mp3",
            1_000,
            new VoiceTimingMetadata(
                [new VoiceCharacterTiming("W", 0, 1_000)],
                [new VoiceSegmentTiming(segment.Id, 0, 1_000)]));
    }

    private async Task UploadAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        await using var content = new MemoryStream(OnePixelPng);
        await media.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("front.png", "image/png", content.Length, content));
    }

    private async Task<OwnerProperty> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var propertyId = await properties.CreateAsync(result.UserId!, ValidProperty);
        return new OwnerProperty(result.UserId!, result.OrganizationId!.Value, propertyId);
    }

    private static PropertyInput ValidProperty => new(
        "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100,
        0.25m, 1998, PropertyType.SingleFamily, "A comfortable home.", ListingStatus.Active);

    private static PropertyStoryContent SafeStory => new(
        "Welcome to 123 Main Street",
        "A comfortable home in Raleigh.",
        "This 3-bedroom, 2.5-bath home offers 2,100 square feet and was built in 1998.",
        ["Listed at $450,000", "A 0.25-acre lot"],
        "Welcome to 123 Main Street. Explore a comfortable home in Raleigh.",
        "Contact the listing team to learn more.",
        "Explore 123 Main Street, listed at $450,000.",
        "Discover 123 Main Street.");

    private sealed record OwnerProperty(string UserId, Guid OrganizationId, Guid PropertyId);
}
