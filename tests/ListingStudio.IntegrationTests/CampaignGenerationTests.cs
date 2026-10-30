using ListingStudio.Application.Audio;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Help;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text;
using Xunit;
using ListingStudio.Domain.Billing;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task SelectedLanguagesAndAccentReachNarrationAndCaptionOutputs()
    {
        var owner = await CreateOwnerAndPropertyAsync("localized-campaign");
        await UploadAsync(owner);
        var callsBefore = fixture.Factory.CampaignLocalizationService.CallCount;
        fixture.Factory.CampaignLocalizationService.Enqueue(request => new CampaignLocalizedCopy(
            request.NarrationSegments.Select(segment =>
                new CampaignLocalizedSegment(segment.Id, $"Narración en español 123: {segment.Text}"))
                .ToArray(),
            request.NarrationSegments.Select(segment =>
                new CampaignLocalizedSegment(segment.Id, $"Sous-titre français 123: {segment.Text}"))
                .ToArray()));
        QueueSuccessfulProviders();
        var preferences = new CampaignNarrationPreferences(
            CampaignNarrationRegion.Mexico,
            CampaignLanguage.Spanish,
            CampaignLanguage.French,
            CampaignVoiceAccent.LatinAmerican,
            LocalizeAgentCard: true);
        var creativeOptions = CampaignCreativeOptions.ForTemplate(
            CampaignMarketingTemplate.PriceDrop,
            priceReductionAmount: 235_000m,
            priceReductionVerified: true);
        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            queued = (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.YouTubeTour,
                creativeOptions,
                preferences))!;
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(preferences, completed.NarrationPreferences);
        Assert.Equal(creativeOptions, completed.CreativeOptions);
        Assert.Equal(callsBefore + 1, fixture.Factory.CampaignLocalizationService.CallCount);
        var voiceRequest = Assert.IsType<VoiceGenerationRequest>(fixture.Factory.VoiceProvider.LastRequest);
        Assert.Equal("es", voiceRequest.SpokenLanguageCode);
        Assert.Contains("Latin American", voiceRequest.AccentInstructions, StringComparison.Ordinal);
        Assert.Contains("Mexico", voiceRequest.AccentInstructions, StringComparison.Ordinal);
        Assert.StartsWith("Narración en español 123", Assert.Single(voiceRequest.Segments).Text, StringComparison.Ordinal);

        await using var verificationScope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == queued.Id);
        Assert.NotNull(persisted.LocalizedCopyJson);
        var deliverable = await dbContext.CampaignDeliverables.AsNoTracking()
            .SingleAsync(candidate => candidate.CampaignGenerationJobId == queued.Id);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            deliverable.SpecificationJson,
            VideoSpecificationJson.Options)!;
        Assert.StartsWith(
            "Narración en español 123",
            Assert.Single(specification.Audio.NarrationSegments).Text,
            StringComparison.Ordinal);
        Assert.Contains(
            specification.Scenes.SelectMany(scene => scene.TextOverlays),
            overlay => overlay.StyleToken == TextOverlayStyle.PopCaption
                && overlay.Text.Contains("SOUS-TITRE FRANÇAIS 123", StringComparison.Ordinal));
        Assert.Contains(
            specification.Scenes.SelectMany(scene => scene.TextOverlays),
            overlay => overlay.Id == "campaign-price-reduction"
                && overlay.Text == "PRICE REDUCED $235,000");
    }

    [Fact]
    public async Task AgentPortraitUploadIsPrivateAndTenantScoped()
    {
        var owner = await CreateOwnerAndPropertyAsync("portrait-owner");
        var outsider = await CreateOwnerAndPropertyAsync("portrait-outsider");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var portraits = scope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
        await using var upload = new MemoryStream(OnePixelPng);

        var saved = await portraits.UploadAsync(
            owner.UserId,
            new AgentPortraitUpload("agent.png", "image/png", upload.Length, upload));

        Assert.Equal(saved, await portraits.GetLatestAsync(owner.UserId));
        Assert.Null(await portraits.GetLatestAsync(outsider.UserId));
        var content = await portraits.OpenReadAsync(owner.UserId, saved.Id);
        Assert.NotNull(content);
        Assert.Equal("image/jpeg", content.ContentType);
        await content.Content.DisposeAsync();
        Assert.Null(await portraits.OpenReadAsync(outsider.UserId, saved.Id));

        var profile = scope.ServiceProvider.GetRequiredService<IAgentProfileService>();
        Assert.False((await profile.GetAsync(owner.UserId)).IncludeAvatarInCampaigns);
        Assert.True((await profile.SetAvatarDefaultAsync(owner.UserId, true)).IncludeAvatarInCampaigns);
        Assert.True((await profile.GetAsync(owner.UserId)).IncludeAvatarInCampaigns);

        var previews = scope.ServiceProvider.GetRequiredService<IAgentAvatarPreviewService>();
        Assert.True(await previews.PrepareAsync(owner.UserId, saved.Id));
        Assert.False(await previews.PrepareAsync(outsider.UserId, saved.Id));
        var preview = await previews.OpenReadAsync(owner.UserId, saved.Id);
        Assert.NotNull(preview);
        Assert.Equal("video/mp4", preview.ContentType);
        await preview.Content.DisposeAsync();
        Assert.Null(await previews.OpenReadAsync(outsider.UserId, saved.Id));
    }

    [Fact]
    public async Task AgentPortraitCropCreatesANewOwnedVersionWithoutChangingTheUpload()
    {
        var owner = await CreateOwnerAndPropertyAsync("portrait-crop-owner");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var portraits = scope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
        await using var upload = new MemoryStream(OnePixelPng);
        var original = await portraits.UploadAsync(
            owner.UserId,
            new AgentPortraitUpload("agent.png", "image/png", upload.Length, upload));

        var cropped = await portraits.CropAsync(
            owner.UserId,
            original.Id,
            new AgentPortraitCrop(0.5m, 0.5m, 1m, AgentPortraitCropShape.Square));

        Assert.NotNull(cropped);
        Assert.NotEqual(original.Id, cropped.Id);
        Assert.Equal(cropped.Width, cropped.Height);
        Assert.True(cropped.Width > 0);
        Assert.Equal(cropped, await portraits.GetLatestAsync(owner.UserId));
        var originalContent = await portraits.OpenReadAsync(owner.UserId, original.Id);
        Assert.NotNull(originalContent);
        await originalContent.Content.DisposeAsync();
    }

    [Fact]
    public async Task ConsentedAgentAvatarIsGeneratedOnceReusedAndTenantScoped()
    {
        var owner = await CreateOwnerAndPropertyAsync("avatar-video-owner");
        var outsider = await CreateOwnerAndPropertyAsync("avatar-video-outsider");
        // The AI agent presenter comes with Professional and Enterprise.
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services, owner.UserId, SubscriptionPlan.Enterprise, fixture.Factory.TimeProvider.GetUtcNow());
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services, outsider.UserId, SubscriptionPlan.Professional, fixture.Factory.TimeProvider.GetUtcNow());
        await UploadAsync(owner);
        Guid portraitId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = scope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using var upload = new MemoryStream(OnePixelPng);
            portraitId = (await portraits.UploadAsync(
                owner.UserId,
                new AgentPortraitUpload("agent.png", "image/png", upload.Length, upload))).Id;
        }

        await using var factory = fixture.CreateFactory(_ => { });
        factory.AgentAvatarVideoProvider.IsEnabled = true;
        factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        factory.StoryGenerator.Enqueue(SafeStory);
        factory.VideoDirector.Enqueue(CreateEditorialPlan);
        factory.VoiceProvider.Enqueue(CreateVoiceResult);
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
        };
        CampaignGenerationResult queued;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            queued = (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.YouTubeTour,
                options,
                CampaignNarrationPreferences.Default))!;
        }

        CampaignGenerationResult? completed = null;
        for (var index = 0; index < 60; index++)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextStageAsync();
            }

            factory.TimeProvider.Advance(TimeSpan.FromSeconds(10));
            await using var readScope = factory.Services.CreateAsyncScope();
            completed = await readScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                .GetAsync(owner.UserId, queued.Id);
            if (completed?.Status == CampaignGenerationStatus.Completed)
            {
                break;
            }
        }

        Assert.NotNull(completed);
        Assert.Equal(CampaignGenerationStatus.Completed, completed.Status);
        Assert.Equal(1, factory.AgentAvatarVideoProvider.CallCount);
        Assert.Equal(1, factory.AgentAvatarNarrationExcerptRenderer.CallCount);
        Assert.Equal(1, factory.VideoRenderer.AgentPresenterInputCount);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await dbContext.AgentAvatarVideos.AsNoTracking()
            .SingleAsync(candidate => candidate.OrganizationId == owner.OrganizationId);
        var service = verificationScope.ServiceProvider.GetRequiredService<IAgentAvatarVideoService>();
        var reused = await service.GetOrCreateAsync(
            owner.OrganizationId,
            owner.PropertyId,
            owner.UserId,
            portraitId,
            stored.VideoNarrationId);
        Assert.NotNull(reused);
        Assert.True(reused.Reused);
        Assert.Equal(1, factory.AgentAvatarVideoProvider.CallCount);

        Assert.Null(await service.GetOrCreateAsync(
            outsider.OrganizationId,
            owner.PropertyId,
            outsider.UserId,
            portraitId,
            stored.VideoNarrationId));
        Assert.Equal(1, factory.AgentAvatarVideoProvider.CallCount);
    }

    [Fact]
    public async Task UploadToGenerateWaitAndDownloadIsDurableIdempotentAndTenantScoped()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-owner");
        var outsider = await CreateOwnerAndPropertyAsync("campaign-outsider");
        var mediaId = await UploadAsync(owner);
        var storyCallsBefore = fixture.Factory.StoryGenerator.CallCount;
        var directorCallsBefore = fixture.Factory.VideoDirector.CallCount;
        var voiceCallsBefore = fixture.Factory.VoiceProvider.CallCount;
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        var generatedClipInputsBefore = fixture.Factory.VideoRenderer.GeneratedClipInputCount;
        var aiVideoCallsBefore = fixture.Factory.AiVideoProvider.CallCount;
        QueueSuccessfulProviders();

        var enqueues = await Task.WhenAll(EnqueueAsync(owner), EnqueueAsync(owner));
        var queued = enqueues[0];
        Assert.Equal(queued.Id, enqueues[1].Id);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var overview = await billing.GetOverviewAsync(owner.UserId);
            Assert.Equal(0, overview.CampaignUsage);
            Assert.Equal(1, overview.ReservedCampaignUsage);
            Assert.Equal(0, overview.RemainingCampaignUsage);
        }

        var secondProperty = await CreateAdditionalPropertyAsync(owner);
        await UploadAsync(secondProperty);
        var reservationError = await Assert.ThrowsAsync<CampaignAllowanceExceededException>(
            () => EnqueueAsync(secondProperty));
        Assert.Contains("still processing", reservationError.Message, StringComparison.OrdinalIgnoreCase);
        string[] generatedAssetPaths;
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
        Assert.Equal(generatedClipInputsBefore + 3, fixture.Factory.VideoRenderer.GeneratedClipInputCount);
        Assert.Equal(aiVideoCallsBefore + 3, fixture.Factory.AiVideoProvider.CallCount);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            foreach (var kind in new[]
            {
                CampaignOutputKind.Hero,
                CampaignOutputKind.Feature,
                CampaignOutputKind.Teaser,
            })
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

            var cachedClip = await scope.ServiceProvider.GetRequiredService<IGeneratedVideoClipService>()
                .GetOrCreateAsync(
                    owner.OrganizationId,
                    owner.PropertyId,
                    mediaId,
                    "slow cinematic push forward",
                    30_000,
                    VideoAspectRatio.Landscape16By9);
            Assert.NotNull(cachedClip);
            Assert.True(cachedClip.Reused);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IGeneratedVideoClipService>()
                .GetOrCreateAsync(
                    outsider.OrganizationId,
                    owner.PropertyId,
                    mediaId,
                    "slow cinematic push forward",
                    30_000,
                    VideoAspectRatio.Landscape16By9));
            Assert.Equal(aiVideoCallsBefore + 3, fixture.Factory.AiVideoProvider.CallCount);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await dbContext.CampaignGenerationJobs
                .AsNoTracking()
                .Include(job => job.Deliverables)
                .SingleAsync(job => job.Id == queued.Id);
            Assert.Equal(owner.OrganizationId, persisted.OrganizationId);
            var billing = await dbContext.OrganizationBillingAccounts
                .AsNoTracking()
                .SingleAsync(account => account.OrganizationId == owner.OrganizationId);
            Assert.Equal(1, billing.CampaignUsage);
            Assert.Equal(0, billing.AdditionalCampaignUsage);
            Assert.Single(await dbContext.CampaignUsageRecords
                .AsNoTracking()
                .Where(record => record.OrganizationId == owner.OrganizationId)
                .ToArrayAsync());
            Assert.All(persisted.Deliverables, item => Assert.Equal(owner.OrganizationId, item.OrganizationId));
            Assert.Equal(3, persisted.Deliverables.Count);
            var clips = await dbContext.GeneratedVideoClips.AsNoTracking()
                .Where(clip => clip.OrganizationId == owner.OrganizationId
                    && clip.PropertyId == owner.PropertyId)
                .ToArrayAsync();
            Assert.Equal(3, clips.Length);
            Assert.All(clips, clip =>
            {
                Assert.Equal(0.125m, clip.EstimatedCostUsd);
                Assert.Equal("fake-provider", clip.Provider);
                Assert.Equal("fake-model", clip.Model);
                using var metadata = JsonDocument.Parse(clip.ProviderMetadataJson);
                Assert.Equal("test", metadata.RootElement.GetProperty("mode").GetString());
            });
            Assert.Equal(0.375m, clips.Sum(clip => clip.EstimatedCostUsd));
            generatedAssetPaths = clips.Select(clip => clip.AssetPath).ToArray();
        }

        var usedError = await Assert.ThrowsAsync<CampaignAllowanceExceededException>(
            () => EnqueueAsync(secondProperty));
        Assert.Contains("allowance is fully used", usedError.Message, StringComparison.OrdinalIgnoreCase);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            Assert.True(await mediaService.DeleteAsync(owner.UserId, owner.PropertyId, mediaId));
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await dbContext.GeneratedVideoClips.AnyAsync(clip => clip.PropertyMediaId == mediaId));
            var assets = scope.ServiceProvider.GetRequiredService<ICampaignAssetStorage>();
            foreach (var path in generatedAssetPaths)
            {
                Assert.Null(await assets.OpenReadAsync(path));
            }
        }
    }

    [Fact]
    public async Task RegeneratingACompletedPropertyDoesNotConsumeAnotherCampaign()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-regeneration-billing");
        await UploadAsync(owner);
        QueueSuccessfulProviders();

        var initial = await EnqueueAsync(owner);
        await ProcessUntilAsync(owner, initial.Id, CampaignGenerationStatus.Completed);

        var regenerated = await EnqueueAsync(owner, CampaignOutputSelection.YouTubeTour);

        Assert.NotEqual(initial.Id, regenerated.Id);
        await using (var reservationScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var overview = await reservationScope.ServiceProvider.GetRequiredService<IBillingService>()
                .GetOverviewAsync(owner.UserId);
            Assert.Equal(1, overview.CampaignUsage);
            Assert.Equal(0, overview.ReservedCampaignUsage);
            Assert.Equal(0, overview.RemainingCampaignUsage);
        }

        await ProcessUntilAsync(owner, regenerated.Id, CampaignGenerationStatus.Completed);

        await using var verificationScope = fixture.Factory.Services.CreateAsyncScope();
        var billing = await verificationScope.ServiceProvider.GetRequiredService<IBillingService>()
            .GetOverviewAsync(owner.UserId);
        Assert.Equal(1, billing.CampaignUsage);
        Assert.Equal(0, billing.ReservedCampaignUsage);
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await dbContext.CampaignUsageRecords.AsNoTracking()
            .Where(record => record.OrganizationId == owner.OrganizationId)
            .ToArrayAsync());
    }

    [Fact]
    public async Task FinishedWalkthroughClipReachesTheRendererBetweenItsTwoPhotos()
    {
        var (owner, front, foyer) = await CreatePropertyWithFinishedWalkthroughClipAsync("campaign-walkthrough");
        var walkthroughInputsBefore = fixture.Factory.VideoRenderer.WalkthroughInputCount;
        var walkthroughShotsBefore = fixture.Factory.VideoRenderer.WalkthroughShotCount;

        var completed = await GenerateTwoPhotoCampaignAsync(owner, front, foyer);

        Assert.True(fixture.Factory.VideoRenderer.WalkthroughInputCount > walkthroughInputsBefore);
        // The short-form cut uses the same clip as a shot of its own.
        Assert.True(fixture.Factory.VideoRenderer.WalkthroughShotCount > walkthroughShotsBefore);
        Assert.Contains(
            await DeliverableSpecificationsAsync(completed.Id),
            json => json.Contains("\"generatedClip\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedWalkthroughCompositionFallsBackToApprovedTourMapPhotos()
    {
        var (owner, front, foyer) = await CreatePropertyWithFinishedWalkthroughClipAsync(
            "campaign-walkthrough-render-fallback");
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        var walkthroughInputsBefore = fixture.Factory.VideoRenderer.WalkthroughInputCount;
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("bad optional transition clip"));

        var completed = await GenerateTwoPhotoCampaignAsync(owner, front, foyer);

        Assert.Equal(CampaignGenerationStatus.Completed, completed.Status);
        Assert.Equal(renderCallsBefore + 3, fixture.Factory.VideoRenderer.CallCount);
        Assert.True(fixture.Factory.VideoRenderer.WalkthroughInputCount > walkthroughInputsBefore);
        Assert.All(completed.Deliverables, deliverable =>
            Assert.Equal(CampaignDeliverableStatus.Rendered, deliverable.Status));
    }

    [Fact]
    public async Task ApprovedDroneIntroOpensTheVideosThatStartOnTheFrontPhoto()
    {
        // A finished intro the agent has not approved is never used.
        var (unapproved, unapprovedFront, unapprovedFoyer) =
            await CreatePropertyWithFinishedWalkthroughClipAsync("campaign-aerial-unapproved");
        await CreateFinishedDroneIntroAsync(unapproved);
        var introsBefore = fixture.Factory.VideoRenderer.AerialIntroInputCount;
        await GenerateTwoPhotoCampaignAsync(unapproved, unapprovedFront, unapprovedFoyer);
        Assert.Equal(introsBefore, fixture.Factory.VideoRenderer.AerialIntroInputCount);

        var (owner, front, foyer) = await CreatePropertyWithFinishedWalkthroughClipAsync("campaign-aerial-intro");
        await CreateFinishedDroneIntroAsync(owner);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
                .ApproveAsync(owner.UserId, owner.PropertyId));
        }

        await GenerateTwoPhotoCampaignAsync(owner, front, foyer);
        Assert.True(fixture.Factory.VideoRenderer.AerialIntroInputCount > introsBefore);
    }

    [Fact]
    public async Task WalkthroughMotionOffLeavesOutTheApprovedDroneIntro()
    {
        var (owner, front, foyer) = await CreatePropertyWithFinishedWalkthroughClipAsync("campaign-aerial-photos-only");
        await CreateFinishedDroneIntroAsync(owner);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
                .ApproveAsync(owner.UserId, owner.PropertyId));
        }

        var introsBefore = fixture.Factory.VideoRenderer.AerialIntroInputCount;
        var photosOnly = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.WalkthroughMotion,
        };

        await GenerateTwoPhotoCampaignAsync(owner, front, foyer, photosOnly);

        Assert.Equal(introsBefore, fixture.Factory.VideoRenderer.AerialIntroInputCount);
    }

    private async Task CreateFinishedDroneIntroAsync(OwnerProperty owner)
    {
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services, owner.UserId, SubscriptionPlan.Professional, fixture.Factory.TimeProvider.GetUtcNow());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var intro = await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
            .RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true);
        var processor = scope.ServiceProvider.GetRequiredService<IAerialIntroProcessor>();
        Assert.Equal(AerialIntroClipStatus.Generating, (await processor.ProcessNextAsync())!.Status);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(intro.Id, WalkthroughVideoState.Completed);
        Assert.Equal(AerialIntroClipStatus.Completed, (await processor.ProcessNextAsync())!.Status);
    }

    private async Task<CampaignGenerationResult> GenerateTwoPhotoCampaignAsync(
        OwnerProperty owner,
        Guid front,
        Guid foyer,
        CampaignCreativeOptions? creativeOptions = null)
    {
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(request => CreateTwoPhotoEditorialPlan(request, front, foyer));
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        const CampaignOutputSelection outputs = CampaignOutputSelection.YouTubeTour | CampaignOutputSelection.TikTok;
        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            queued = creativeOptions is null
                ? (await service.EnqueueAsync(owner.UserId, owner.PropertyId, outputs))!
                : (await service.EnqueueAsync(
                    owner.UserId,
                    owner.PropertyId,
                    outputs,
                    creativeOptions,
                    CampaignNarrationPreferences.Default))!;
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        Assert.All(completed.Deliverables, deliverable =>
            Assert.Equal(CampaignDeliverableStatus.Rendered, deliverable.Status));
        return completed;
    }

    private async Task<string[]> DeliverableSpecificationsAsync(Guid jobId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.CampaignGenerationJobId == jobId)
            .Select(deliverable => deliverable.SpecificationJson)
            .ToArrayAsync();
    }

    [Fact]
    public async Task WalkthroughMotionOffLeavesOutTheAiWalkthroughClips()
    {
        var (owner, front, foyer) = await CreatePropertyWithFinishedWalkthroughClipAsync("campaign-walkthrough-off");
        var walkthroughInputsBefore = fixture.Factory.VideoRenderer.WalkthroughInputCount;
        var walkthroughShotsBefore = fixture.Factory.VideoRenderer.WalkthroughShotCount;
        var photosOnly = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.WalkthroughMotion,
        };

        var completed = await GenerateTwoPhotoCampaignAsync(owner, front, foyer, photosOnly);

        Assert.Equal(walkthroughInputsBefore, fixture.Factory.VideoRenderer.WalkthroughInputCount);
        Assert.Equal(walkthroughShotsBefore, fixture.Factory.VideoRenderer.WalkthroughShotCount);
        Assert.DoesNotContain(
            await DeliverableSpecificationsAsync(completed.Id),
            json => json.Contains("\"generatedClip\"", StringComparison.Ordinal));
    }

    private async Task<(OwnerProperty Owner, Guid Front, Guid Foyer)> CreatePropertyWithFinishedWalkthroughClipAsync(
        string name)
    {
        var owner = await CreateOwnerAndPropertyAsync(name);
        var front = await UploadAsync(owner);
        Guid foyer;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await using var content = new MemoryStream(OnePixelPng);
            foyer = await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>().UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload("foyer.png", "image/png", content.Length, content, WalkthroughStoryboard.EntryFoyer));
            var transitions = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();
            Assert.Equal(1, await transitions.QueueAsync(owner.UserId, owner.PropertyId));
            var transitionId = Assert.Single(await transitions.ListAsync(owner.UserId, owner.PropertyId)).TransitionId!.Value;
            var processor = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionProcessor>();
            Assert.Equal(WalkthroughTransitionStatus.Generating, (await processor.ProcessNextAsync())!.Status);
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
            fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(transitionId, WalkthroughVideoState.Completed);
            Assert.Equal(WalkthroughTransitionStatus.Completed, (await processor.ProcessNextAsync())!.Status);
        }

        return (owner, front, foyer);
    }

    [Fact]
    public async Task RecommendedOutputsRenderTheVerticalVideoOnce()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-recommended-outputs");
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);

        var queued = await EnqueueAsync(owner, CampaignOutputSelectionPolicy.Recommended);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        // TikTok, Reels and Shorts share one edit, so it is rendered once.
        Assert.Collection(
            completed.Deliverables.OrderBy(item => item.Kind),
            item => AssertPlatformCut(item, CampaignOutputKind.TikTok, 30, "9:16"),
            item => AssertPlatformCut(item, CampaignOutputKind.YouTubeTour, 60, "16:9"),
            item => AssertPlatformCut(item, CampaignOutputKind.FacebookFeed, 30, "16:9"));
        Assert.Collection(
            completed.StillPosts.OrderBy(item => item.Kind),
            item => AssertStillPost(item, CampaignStillPostKind.FacebookSquare, 1080, 1080),
            item => AssertStillPost(item, CampaignStillPostKind.FacebookPortrait, 1080, 1350));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        var post = await campaigns.OpenStillPostAsync(
            owner.UserId,
            queued.Id,
            CampaignStillPostKind.FacebookSquare);
        Assert.NotNull(post);
        Assert.Equal("image/jpeg", post.ContentType);
        Assert.EndsWith(".jpg", post.FileName, StringComparison.Ordinal);
        await post.Content.DisposeAsync();

        var outsider = await CreateOwnerAndPropertyAsync("campaign-still-post-outsider");
        Assert.Null(await campaigns.OpenStillPostAsync(
            outsider.UserId,
            queued.Id,
            CampaignStillPostKind.FacebookSquare));
    }

    private static void AssertStillPost(
        CampaignStillPostResult post,
        CampaignStillPostKind kind,
        int width,
        int height)
    {
        Assert.Equal(kind, post.Kind);
        Assert.Equal(CampaignStillPostStatus.Rendered, post.Status);
        Assert.Equal(width, post.Width);
        Assert.Equal(height, post.Height);
        Assert.True(post.FileSize > 0);
        Assert.False(string.IsNullOrWhiteSpace(post.Caption));
    }

    [Fact]
    public async Task SelectedPlatformOutputsProduceOnlyTheRequestedCuts()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-platform-outputs");
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        var shortFormSegments = new List<string>();
        fixture.Factory.VoiceProvider.Enqueue(request =>
        {
            shortFormSegments.AddRange(request.Segments.Select(segment => segment.Id));
            return CreateSegmentedVoiceResult(request);
        });

        // Campaigns created before the shared vertical video can still ask for each vertical output.
        const CampaignOutputSelection everyPlatform = CampaignOutputSelection.TikTok
            | CampaignOutputSelection.InstagramReel
            | CampaignOutputSelection.YouTubeShort
            | CampaignOutputSelection.YouTubeTour
            | CampaignOutputSelection.FacebookFeed;
        var queued = await EnqueueAsync(owner, everyPlatform);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(everyPlatform, completed.RequestedOutputs);
        Assert.Equal(5, completed.Deliverables.Count);
        Assert.Collection(
            completed.Deliverables.OrderBy(item => item.Kind),
            item => AssertPlatformCut(item, CampaignOutputKind.TikTok, 30, "9:16"),
            item => AssertPlatformCut(item, CampaignOutputKind.InstagramReel, 30, "9:16"),
            item => AssertPlatformCut(item, CampaignOutputKind.YouTubeShort, 30, "9:16"),
            item => AssertPlatformCut(item, CampaignOutputKind.YouTubeTour, 60, "16:9"),
            item => AssertPlatformCut(item, CampaignOutputKind.FacebookFeed, 30, "16:9"));

        // TikTok, Reels and Shorts share one beat-cut edit voiced with its own short voiceover.
        Assert.Equal("short-story.openingHook", shortFormSegments[0]);
        Assert.Equal("short-story.closingCta", shortFormSegments[^1]);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deliverables = await db.CampaignDeliverables.AsNoTracking()
            .Where(item => item.CampaignGenerationJobId == queued.Id)
            .ToListAsync();
        var tikTok = deliverables.Single(item => item.Kind == CampaignOutputKind.TikTok);
        var tour = deliverables.Single(item => item.Kind == CampaignOutputKind.YouTubeTour);
        Assert.NotEqual(tour.VideoNarrationId, tikTok.VideoNarrationId);
        Assert.Equal(tikTok.VideoNarrationId, deliverables.Single(item => item.Kind == CampaignOutputKind.InstagramReel).VideoNarrationId);
        Assert.Contains("\"hookTitle\"", tikTok.SpecificationJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortFormNarrationOverrunReducesTheScriptBeforeFallingBackToMusicOnly()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-short-narration-overrun");
        await UploadAsync(owner);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        var shortVoiceCalls = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixture.Factory.VoiceProvider.Enqueue(request =>
            {
                shortVoiceCalls++;
                var spokenMs = 31_000;
                return new VoiceGenerationResult(
                    [1, 2, 3, 4],
                    "audio/mpeg",
                    ".mp3",
                    spokenMs,
                    new VoiceTimingMetadata(
                        [new VoiceCharacterTiming("A", 0, spokenMs)],
                        request.Segments.Select(segment =>
                            new VoiceSegmentTiming(segment.Id, 0, spokenMs)).ToArray()));
            });
        }

        var queued = await EnqueueAsync(owner, CampaignOutputSelectionPolicy.Recommended);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(3, shortVoiceCalls);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var vertical = await db.CampaignDeliverables.AsNoTracking()
            .SingleAsync(item => item.CampaignGenerationJobId == queued.Id
                && item.Kind == CampaignOutputKind.TikTok);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            vertical.SpecificationJson,
            VideoSpecificationJson.Options)!;
        Assert.Empty(specification.Audio.NarrationSegments);
    }

    private static VoiceGenerationResult CreateSegmentedVoiceResult(VoiceGenerationRequest request) => new(
        [1, 2, 3, 4],
        "audio/mpeg",
        ".mp3",
        request.Segments.Count * 1_000,
        new VoiceTimingMetadata(
            [new VoiceCharacterTiming("W", 0, 1_000)],
            request.Segments.Select((segment, index) =>
                new VoiceSegmentTiming(segment.Id, index * 1_000, index * 1_000 + 900)).ToArray()));

    [Fact]
    public async Task FullNarratedTourRequiresAnAcceptedLongScript()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-full-tour-validation");
        await UploadAsync(owner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnqueueAsync(owner, CampaignOutputSelection.FullNarratedTour));

        Assert.Contains("accepted narration script", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("145 words", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptedLongScriptAddsNarrationLengthDeliverable()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-long-form");
        await UploadAsync(owner);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var scripts = scope.ServiceProvider.GetRequiredService<IPropertyNarrationScriptService>();
            var text = string.Join(' ', Enumerable.Repeat("welcome", 1_000));
            var bytes = Encoding.UTF8.GetBytes(text);
            await using var content = new MemoryStream(bytes);
            var scriptId = await scripts.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyNarrationScriptUpload("long-form.txt", "text/plain", bytes.Length, content));
            Assert.True(await scripts.SetMarketingUseAcceptedAsync(
                owner.UserId, owner.PropertyId, scriptId, accepted: true));
        }

        var directorCallsBefore = fixture.Factory.VideoDirector.CallCount;
        var voiceCallsBefore = fixture.Factory.VoiceProvider.CallCount;
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VideoDirector.Enqueue(CreateLongFormEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(_ => new VoiceGenerationResult(
            [1, 2, 3, 4], "audio/mpeg", ".mp3", 1_000, null));

        var queued = await EnqueueAsync(owner);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(4, completed.Deliverables.Count);
        var longForm = Assert.Single(completed.Deliverables, item => item.Kind == CampaignOutputKind.LongForm);
        Assert.Equal(420, longForm.DurationSeconds);
        Assert.Equal(CampaignDeliverableStatus.Rendered, longForm.Status);
        Assert.Equal(directorCallsBefore + 2, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 2, fixture.Factory.VoiceProvider.CallCount);
        Assert.Equal(renderCallsBefore + 4, fixture.Factory.VideoRenderer.CallCount);
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
        var aiVideoCallsBefore = fixture.Factory.AiVideoProvider.CallCount;
        QueueSuccessfulProviders();
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("first fake failure"));
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("second fake failure"));
        fixture.Factory.VideoRenderer.EnqueueFailure(new VideoRenderException("third fake failure"));

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.RenderHero, failed.CurrentStage);
        Assert.Contains("rendered", failed.LastError, StringComparison.OrdinalIgnoreCase);
        // Retried attempts stay quiet; only the failure that stops the campaign tells the member who started it.
        var stopped = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.Campaign, UserNotificationOutcome.Failed), (stopped.Kind, stopped.Outcome));
        Assert.StartsWith(
            "The campaign for 123 Main Street stopped while rendering videos. You can resume it on the Campaign tab.",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal($"/properties/{owner.PropertyId}?tab=campaign", stopped.Link);
        Assert.Equal(storyCallsBefore + 1, fixture.Factory.StoryGenerator.CallCount);
        Assert.Equal(directorCallsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 1, fixture.Factory.VoiceProvider.CallCount);
        Assert.Equal(aiVideoCallsBefore + 3, fixture.Factory.AiVideoProvider.CallCount);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var overview = await billing.GetOverviewAsync(owner.UserId);
            Assert.Equal(0, overview.CampaignUsage);
            Assert.Equal(0, overview.ReservedCampaignUsage);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            Assert.True(await campaigns.RetryAsync(owner.UserId, queued.Id));
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        Assert.Equal(CampaignGenerationStatus.Completed, completed.Status);
        var inbox = await NotificationInbox.ForAsync(fixture, owner.UserId);
        Assert.Equal(2, inbox.UnreadCount);
        var ready = Assert.Single(inbox.Items, item => item.Outcome == UserNotificationOutcome.Succeeded);
        Assert.Equal("Campaign ready", ready.Title);
        Assert.Equal("The marketing videos for 123 Main Street are ready to review and download.", ready.Message);
        Assert.Equal(storyCallsBefore + 1, fixture.Factory.StoryGenerator.CallCount);
        Assert.Equal(directorCallsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        Assert.Equal(voiceCallsBefore + 1, fixture.Factory.VoiceProvider.CallCount);
        Assert.Equal(aiVideoCallsBefore + 3, fixture.Factory.AiVideoProvider.CallCount);
        Assert.Equal(renderCallsBefore + 6, fixture.Factory.VideoRenderer.CallCount);
    }

    [Fact]
    public async Task RetryReloadsCampaignChangedByWorkerAfterServiceTrackedEarlierState()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-stale-retry");
        await UploadAsync(owner);
        await using var browserScope = fixture.Factory.Services.CreateAsyncScope();
        var campaigns = browserScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        var queued = (await campaigns.EnqueueAsync(owner.UserId, owner.PropertyId))!;

        await using (var workerScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = workerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == queued.Id);
            var now = DateTimeOffset.UtcNow;
            job.BeginStage(now, TimeSpan.FromMinutes(1));
            job.Fail("worker failure", now);
            await dbContext.SaveChangesAsync();
        }

        Assert.True(await campaigns.RetryAsync(owner.UserId, queued.Id));
        var retried = await campaigns.GetAsync(owner.UserId, queued.Id);
        Assert.NotNull(retried);
        Assert.Equal(CampaignGenerationStatus.Queued, retried.Status);
        Assert.Null(retried.LastError);
        Assert.True(await campaigns.CancelAsync(owner.UserId, queued.Id));
    }

    [Fact]
    public async Task StoryTimeoutIsRetriedAndReportedWithoutProviderDetails()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-story-timeout");
        await UploadAsync(owner);
        var storyCallsBefore = fixture.Factory.StoryGenerator.CallCount;
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
        {
            fixture.Factory.StoryGenerator.Enqueue(new TimeoutException("sensitive provider timeout details"));
        }

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.GenerateStory, failed.CurrentStage);
        Assert.Equal("An external provider request timed out.", failed.LastError);
        Assert.DoesNotContain("sensitive", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            storyCallsBefore + ICampaignGenerationProcessor.MaximumStageAttempts,
            fixture.Factory.StoryGenerator.CallCount);
    }

    [Fact]
    public async Task InvalidMasterPlanGetsOneGuidedRepairWithoutBlindStageRetries()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-invalid-plan");
        await UploadAsync(owner);
        await UploadAsync(owner);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        var directorCallsBefore = fixture.Factory.VideoDirector.CallCount;
        static DirectedEditorialPlan InvalidPlan(VideoDirectionRequest request)
        {
            var valid = CreateEditorialPlan(request);
            var first = valid.Scenes[0];
            return valid with { Scenes = [first with { DurationMs = first.DurationMs - 1 }] };
        }
        fixture.Factory.VideoDirector.Enqueue(InvalidPlan);
        fixture.Factory.VideoDirector.EnqueueRepair((request, feedback) =>
        {
            Assert.NotEmpty(feedback);
            return InvalidPlan(request);
        });

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.GenerateMasterVideoPlan, failed.CurrentStage);
        Assert.Contains("guided repair", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interior and exterior photos", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, failed.Stages.Single(stage =>
            stage.Stage == CampaignGenerationStage.GenerateMasterVideoPlan).AttemptCount);
        Assert.Equal(directorCallsBefore + 2, fixture.Factory.VideoDirector.CallCount);
    }

    [Fact]
    public async Task VoiceFailureIsRetriedAndStopsAtNarrationCheckpoint()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-voice-failure");
        await UploadAsync(owner);
        var voiceCallsBefore = fixture.Factory.VoiceProvider.CallCount;
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
        {
            fixture.Factory.VoiceProvider.Enqueue(_ =>
                throw new HttpRequestException("sensitive voice provider failure"));
        }

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.GenerateNarration, failed.CurrentStage);
        Assert.Equal("An external provider request failed.", failed.LastError);
        Assert.Equal(
            voiceCallsBefore + ICampaignGenerationProcessor.MaximumStageAttempts,
            fixture.Factory.VoiceProvider.CallCount);
    }

    [Fact]
    public async Task AiVideoTimeoutIsRetriedAndStopsBeforeRendering()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-video-timeout");
        await UploadAsync(owner);
        var aiVideoCallsBefore = fixture.Factory.AiVideoProvider.CallCount;
        var renderCallsBefore = fixture.Factory.VideoRenderer.CallCount;
        QueueSuccessfulProviders();
        for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
        {
            fixture.Factory.AiVideoProvider.EnqueueFailure(
                new TimeoutException("sensitive video provider timeout"));
        }

        var queued = await EnqueueAsync(owner);
        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignGenerationStage.GenerateRequiredAiVideo, failed.CurrentStage);
        Assert.Equal("An external provider request timed out.", failed.LastError);
        Assert.Equal(
            aiVideoCallsBefore + ICampaignGenerationProcessor.MaximumStageAttempts,
            fixture.Factory.AiVideoProvider.CallCount);
        Assert.Equal(renderCallsBefore, fixture.Factory.VideoRenderer.CallCount);
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

    [Fact]
    public async Task VideoChatRendersOnlyThatVideoKeepsTheOldVersionForUndoAndIsLoggedForAdmins()
    {
        var owner = await CreateOwnerAndPropertyAsync("campaign-video-chat");
        await UploadAsync(owner);
        var second = await UploadAsync(owner);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        QueueSuccessfulProviders();
        var queued = await EnqueueAsync(owner, CampaignOutputSelection.YouTubeTour | CampaignOutputSelection.FacebookFeed);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        var originalSpecifications = await DeliverableSpecificationsAsync(completed.Id);

        fixture.Factory.VideoChatAssistant.Enqueue(request =>
        {
            Assert.Equal("YouTube tour", request.VideoName);
            Assert.Contains(request.ListingPhotos, photo => photo.Id == second);
            Assert.Equal("Use the other photo first and switch to upbeat music", request.Conversation[^1].Text);
            // The chat can explain how the video was made, not only change it.
            Assert.Contains(request.Production!, note => note.Topic == "How it was edited"
                && note.Detail.Contains("master tour plan", StringComparison.Ordinal));
            Assert.Contains(request.Production!, note => note.Topic == "Voiceover words");
            return new CampaignVideoChatReply(
                "I'll open on the other photo and switch to the upbeat track.",
                [
                    new CampaignVideoEdit(CampaignVideoEditKind.SwapShot, Shot: 1, PhotoId: second),
                    new CampaignVideoEdit(CampaignVideoEditKind.ChangeMusic, Music: CampaignSoundtrack.UpbeatLifestyle),
                    new CampaignVideoEdit(CampaignVideoEditKind.RemoveText, TextId: "not-in-this-video"),
                ]);
        });

        CampaignVideoChat chat;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
            chat = (await service.SendAsync(
                owner.UserId,
                completed.Id,
                CampaignOutputKind.YouTubeTour,
                "Use the other photo first and switch to upbeat music"))!;
            var stranger = await CreateOwnerAndPropertyAsync("campaign-video-chat-stranger");
            Assert.Null(await service.GetAsync(stranger.UserId, completed.Id, CampaignOutputKind.YouTubeTour));
        }

        var proposal = chat.Messages[^1];
        Assert.Equal(CampaignVideoChatRole.Assistant, proposal.Role);
        Assert.Equal(2, proposal.ProposedChanges.Count);
        Assert.Contains("couldn't do all of it", proposal.Text, StringComparison.Ordinal);
        Assert.Equal(CampaignVideoChatProposalState.Ready, proposal.Proposal);
        Assert.Equal(CampaignVideoRevision.FreeRendersPerVideo, chat.RendersLeft);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
            chat = (await service.RenderAsync(owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour, proposal.Id))!;
            Assert.Equal(CampaignVideoRevisionStatus.Queued, chat.ActiveRevisionStatus);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RenderAsync(owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour, proposal.Id));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>()
                .ProcessNextRevisionAsync();
            Assert.NotNull(run);
            Assert.True(run.Succeeded, run.Error);
        }

        var rendered = fixture.Factory.VideoRenderer.LastRequest!.Specification;
        Assert.Equal(second, rendered.Scenes[0].VisualSource.PropertyMediaId);
        Assert.Equal(CampaignCreativeDirector.UpbeatLifestyleAssetId, rendered.Audio.Music.AssetId);
        var afterRender = await DeliverableSpecificationsAsync(completed.Id);
        // Only the video that was discussed changed.
        Assert.Single(afterRender.Except(originalSpecifications));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
            chat = (await service.GetAsync(owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour))!;
            Assert.Equal(2, chat.Version);
            Assert.True(chat.CanUndo);
            Assert.Equal(CampaignVideoRevision.FreeRendersPerVideo - 1, chat.RendersLeft);
            Assert.Equal(CampaignVideoChatProposalState.Rendered, chat.Messages[^1].Proposal);
            var campaign = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                .GetAsync(owner.UserId, completed.Id);
            Assert.Equal(2, campaign!.Deliverables.Single(item => item.Kind == CampaignOutputKind.YouTubeTour).Version);
            Assert.Equal(1, campaign.Deliverables.Single(item => item.Kind == CampaignOutputKind.FacebookFeed).Version);
            var download = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                .OpenDeliverableAsync(owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour);
            Assert.NotNull(download);
            await download.Content.DisposeAsync();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().UserNotifications
                .AnyAsync(notification => notification.UserId == owner.UserId && notification.Title == "Video updated"));

            var renderedMessageId = chat.Messages[^1].Id;
            Assert.Null(chat.Messages[^1].FixConfirmed);
            chat = (await service.ConfirmFixAsync(
                owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour, renderedMessageId, false, "Music is still too slow"))!;
            Assert.False(chat.Messages[^1].FixConfirmed);
            chat = (await service.ConfirmFixAsync(
                owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour, renderedMessageId, true, "  Looks right now  "))!;
            Assert.True(chat.Messages[^1].FixConfirmed);
            Assert.Equal("Looks right now", chat.Messages[^1].FixFeedback);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmFixAsync(
                owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour, chat.Messages[^2].Id, true, null));

            chat = (await service.UndoAsync(owner.UserId, completed.Id, CampaignOutputKind.YouTubeTour))!;
            Assert.Equal(1, chat.Version);
            Assert.False(chat.CanUndo);
            Assert.Equal(CampaignVideoChatProposalState.Undone, chat.Messages[^1].Proposal);
        }

        Assert.Equal(
            originalSpecifications.Order(StringComparer.Ordinal),
            (await DeliverableSpecificationsAsync(completed.Id)).Order(StringComparer.Ordinal));

        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<Application.Administration.IPlatformAdminDashboardService>();
            var rows = await admin.GetVideoChatRequestsAsync(
                Application.Administration.AdminDateRange.LastDays(90, fixture.Factory.TimeProvider));
            var row = Assert.Single(rows, item => item.Request == "Use the other photo first and switch to upbeat music");
            Assert.Equal("YouTube tour", row.Video);
            Assert.Equal("Rendered, then undone", row.Outcome);
            Assert.Equal(2, row.ProposedChanges.Count);
            Assert.StartsWith("I'll open on the other photo", row.AssistantReply, StringComparison.Ordinal);
            Assert.StartsWith("123 Main Street", row.PropertyAddress, StringComparison.Ordinal);
            Assert.NotNull(row.UserEmail);
            Assert.True(row.FixConfirmed);
            Assert.Equal("Looks right now", row.FixFeedback);
            Assert.NotNull(row.FixConfirmedAtUtc);
        }
    }

    [Fact]
    public async Task HelpChatKnowsThePropertyListsItsFinishedVideosAndIsLoggedForAdmins()
    {
        var owner = await CreateOwnerAndPropertyAsync("help-chat");
        var stranger = await CreateOwnerAndPropertyAsync("help-chat-stranger");
        var selectedPhoto = await UploadAsync(owner);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        QueueSuccessfulProviders();
        var queued = await EnqueueAsync(owner, CampaignOutputSelection.YouTubeTour | CampaignOutputSelection.FacebookFeed);
        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IHelpChatService>();
            var videos = await service.GetVideosAsync(owner.UserId, owner.PropertyId);
            Assert.Equal(
                [CampaignOutputKind.YouTubeTour, CampaignOutputKind.FacebookFeed],
                videos.Select(video => video.Kind).Order().ToArray());
            Assert.All(videos, video => Assert.Equal(completed.Id, video.JobId));
            Assert.Contains(videos, video => video.Name == "YouTube tour");
            Assert.Empty(await service.GetVideosAsync(stranger.UserId, owner.PropertyId));

            var empty = await service.GetAsync(owner.UserId);
            Assert.Null(empty.ConversationId);
            Assert.Empty(empty.Messages);

            fixture.Factory.HelpChatAssistant.Enqueue(request =>
            {
                Assert.Equal("/properties/x?tab=campaign", request.PagePath);
                Assert.StartsWith("123 Main Street", request.Property!.Address, StringComparison.Ordinal);
                Assert.Equal("Completed", request.Property.LatestCampaignStatus);
                Assert.Contains("YouTube tour", request.Property.FinishedVideos);
                Assert.NotNull(request.Photo);
                Assert.Equal("Exterior front", request.Photo.TourStop);
                return new HelpChatAssistantReply(
                    "I can fix the selected photo's brightness and color.",
                    HelpChatPhotoAction.FixBrightnessAndColor);
            });
            var chat = await service.SendAsync(
                owner.UserId,
                null,
                "  How was my YouTube video made?  ",
                "/properties/x?tab=campaign",
                owner.PropertyId,
                selectedPhoto);
            Assert.NotNull(chat.ConversationId);
            Assert.Equal(2, chat.Messages.Count);
            Assert.Equal("How was my YouTube video made?", chat.Messages[0].Text);
            Assert.Equal("I can fix the selected photo's brightness and color.", chat.Messages[1].Text);
            Assert.Equal(HelpChatPhotoAction.FixBrightnessAndColor, chat.ProposedPhotoAction);
            Assert.Equal(IHelpChatService.MaximumMessagesPerDay - 1, chat.MessagesLeftToday);

            // Another workspace's property gives the assistant no context.
            fixture.Factory.HelpChatAssistant.Enqueue(request =>
            {
                Assert.Null(request.Property);
                Assert.Null(request.Photo);
                Assert.Equal(3, request.Conversation.Count);
                return "Open the property first.";
            });
            chat = await service.SendAsync(
                owner.UserId,
                chat.ConversationId,
                "And this one?",
                "/properties/y",
                stranger.PropertyId,
                selectedPhoto);
            Assert.Equal(4, chat.Messages.Count);
            Assert.Equal(chat.ConversationId, (await service.GetAsync(owner.UserId)).ConversationId);
            Assert.Empty((await service.GetAsync(stranger.UserId)).Messages);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SendAsync(owner.UserId, chat.ConversationId, "   ", null, null));

            fixture.Factory.TimeProvider.Advance(IHelpChatService.ConversationIdleTimeout + TimeSpan.FromMinutes(1));
            Assert.Empty((await service.GetAsync(owner.UserId)).Messages);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<Application.Administration.IPlatformAdminDashboardService>();
            var rows = await admin.GetHelpChatQuestionsAsync(
                Application.Administration.AdminDateRange.LastDays(90, fixture.Factory.TimeProvider));
            var row = Assert.Single(rows, item => item.Question == "How was my YouTube video made?");
            Assert.Equal("I can fix the selected photo's brightness and color.", row.AssistantReply);
            Assert.Equal("/properties/x?tab=campaign", row.PagePath);
            Assert.StartsWith("123 Main Street", row.PropertyAddress, StringComparison.Ordinal);
            Assert.NotNull(row.UserEmail);
            var other = Assert.Single(rows, item => item.Question == "And this one?");
            Assert.Null(other.PropertyAddress);
            Assert.Equal("Open the property first.", other.AssistantReply);
        }
    }

    [Fact]
    public async Task HelpChatStopsAtTheDailyLimit()
    {
        var owner = await CreateOwnerAndPropertyAsync("help-chat-limit");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHelpChatService>();
        Guid? conversation = null;
        for (var index = 0; index < IHelpChatService.MaximumMessagesPerDay; index++)
        {
            fixture.Factory.HelpChatAssistant.Enqueue(_ => "Answer.");
            conversation = (await service.SendAsync(owner.UserId, conversation, $"Question {index}", "/auth", null)).ConversationId;
        }

        var limited = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendAsync(owner.UserId, conversation, "One more", "/auth", null));
        Assert.Contains("daily limit", limited.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await service.GetAsync(owner.UserId)).MessagesLeftToday);
    }

    private async Task<CampaignGenerationResult> ProcessUntilAsync(
        OwnerProperty owner,
        Guid jobId,
        CampaignGenerationStatus expected)
    {
        CampaignGenerationResult? latest = null;
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
            latest = result;
            if (result.Status == expected)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Campaign {jobId} did not reach {expected}; latest state was {latest?.Status} at {latest?.CurrentStage}: {latest?.LastError}");
    }

    private async Task<CampaignGenerationResult> EnqueueAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
            .EnqueueAsync(owner.UserId, owner.PropertyId))!;
    }

    private async Task<CampaignGenerationResult> EnqueueAsync(
        OwnerProperty owner,
        CampaignOutputSelection requestedOutputs)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
            .EnqueueAsync(owner.UserId, owner.PropertyId, requestedOutputs))!;
    }

    private static void AssertPlatformCut(
        CampaignDeliverableResult item,
        CampaignOutputKind kind,
        int durationSeconds,
        string aspectRatio)
    {
        Assert.Equal(kind, item.Kind);
        Assert.Equal(CampaignDeliverableStatus.Rendered, item.Status);
        Assert.Equal(durationSeconds, item.DurationSeconds);
        Assert.Equal(aspectRatio, item.AspectRatio);
    }

    private void QueueSuccessfulProviders()
    {
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
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
                    new VisualSource(
                        VisualSourceKind.GenerativeMotionRequest,
                        media.MediaId,
                        null,
                        media.MediaId,
                        "slow cinematic push forward"),
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

    private static DirectedEditorialPlan CreateTwoPhotoEditorialPlan(
        VideoDirectionRequest request,
        Guid firstMediaId,
        Guid secondMediaId)
    {
        var plan = CreateEditorialPlan(request);
        var scenes = plan.Scenes.ToArray();
        scenes[0] = scenes[0] with
        {
            VisualSource = new VisualSource(VisualSourceKind.PropertyMedia, firstMediaId, null, null, null),
        };
        scenes[1] = scenes[1] with
        {
            VisualSource = new VisualSource(VisualSourceKind.PropertyMedia, secondMediaId, null, null, null),
        };
        return plan with { Scenes = scenes };
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

    private static DirectedEditorialPlan CreateLongFormEditorialPlan(VideoDirectionRequest request)
    {
        var accepted = Assert.IsType<AcceptedNarrationScriptInput>(request.AcceptedNarrationScript);
        Assert.NotEmpty(accepted.Segments);
        var media = request.Media[0];
        var viewportHeight = 9m * media.Width / (16m * media.Height);
        var viewport = viewportHeight <= 1
            ? new NormalizedRect(0, 0, 1, viewportHeight)
            : new NormalizedRect(0, 0, 16m * media.Height / (9m * media.Width), 1);
        var duration = (int)request.RequestedDuration * 1_000;
        var narrationWindow = duration - 6_000;
        var narrationDuration = narrationWindow / accepted.Segments.Count;
        var narration = accepted.Segments.Select((segment, index) => new NarrationSegment(
            $"long-form-narration-{index + 1}",
            500 + index * narrationDuration,
            index == accepted.Segments.Count - 1
                ? narrationWindow - index * narrationDuration
                : narrationDuration,
            segment.Text,
            segment.Key)).ToArray();
        return new DirectedEditorialPlan(
            new AudioPlan(
                narration,
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    duration,
                    new VisualSource(VisualSourceKind.PropertyMedia, media.MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(MotionKind.KenBurns, viewport, viewport, MotionEasing.EaseInOut),
                    [new TextOverlay(
                        "long-form-closing-cta",
                        request.CallToAction.Text,
                        request.CallToAction.GroundingKey,
                        duration - 5_000,
                        4_000,
                        OverlayAnchor.BottomCenter,
                        new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                        TextOverlayStyle.ClosingCta)],
                    [],
                    narration.Select(item => item.Id).ToArray()),
            ]);
    }

    private async Task<Guid> UploadAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        await using var content = new MemoryStream(OnePixelPng);
        return await media.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("front.png", "image/png", content.Length, content, WalkthroughStoryboard.ExteriorFront));
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

    private async Task<OwnerProperty> CreateAdditionalPropertyAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var propertyId = await properties.CreateAsync(owner.UserId, ValidProperty with
        {
            Address1 = $"{Random.Shared.Next(200, 999)} Second Street",
        });
        return owner with { PropertyId = propertyId };
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

    private static PropertyMediaAnalysis SuccessfulAnalysis => new(
        PropertyMediaCategory.FrontExterior,
        "Exterior",
        91,
        96,
        true,
        false,
        false,
        [],
        "Front exterior of a detached home.",
        0);

    private sealed record OwnerProperty(string UserId, Guid OrganizationId, Guid PropertyId);
}

