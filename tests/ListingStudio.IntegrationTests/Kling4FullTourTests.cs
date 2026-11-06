using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Providers;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task KlingNativeRequestRejectsRatherThanReusingAnActiveEstablishedCampaignForTheProperty()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-active-strategy");
        await GrantReviewAdminAsync(owner.UserId);
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using (var portrait = new MemoryStream(OnePixelPng))
            {
                portraitId = (await portraits.UploadAsync(
                    owner.UserId,
                    new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
            }

            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4NativePresenterV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        var established = (await campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.FacebookFeed,
            CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Established))!;
        var nativeOptions = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.TikTok,
            nativeOptions,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Kling4NativePresenter,
            new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree, VerticalCampaignGoal.MaximizeReach)));

        Assert.Contains("different settings", exception.Message, StringComparison.Ordinal);
        Assert.Contains("selected video strategy was not changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            CampaignVideoStrategy.Established,
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignGenerationJobs
                .Where(candidate => candidate.Id == established.Id)
                .Select(candidate => candidate.VideoStrategy)
                .SingleAsync());

        await campaigns.CancelAsync(owner.UserId, established.Id);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4NativePresenterV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
    }

    [Fact]
    public async Task KlingFullTourRequestIsStoredOnlyWhileItsWorkspaceReleaseSwitchIsOn()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-strategy");
        await GrantReviewAdminAsync(owner.UserId);
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload(
                    "exterior.png",
                    "image/png",
                    exterior.Length,
                    exterior,
                    WalkthroughStoryboard.ExteriorFront));
            await using var entry = new MemoryStream(OnePixelPng);
            await media.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload("entry.png", "image/png", entry.Length, entry, "Entry foyer"));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);

        Assert.True(await campaigns.IsKlingFullTourEnabledAsync(owner.UserId));
        var pilot = (await campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.TikTok,
            CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Kling4FullTour,
            new VerticalLibraryRequest(
                VerticalVideoSelection.RecommendedThree,
                VerticalCampaignGoal.MaximizeReach)))!;
        Assert.Equal(CampaignVideoStrategy.Kling4FullTour, pilot.VideoStrategy);
        Assert.Equal(VerticalCampaignGoal.MaximizeReach, pilot.VerticalRequest?.Goal);
        Assert.Equal(
            CampaignVideoStrategy.Kling4FullTour,
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignGenerationJobs
                .Where(candidate => candidate.Id == pilot.Id)
                .Select(candidate => candidate.VideoStrategy)
                .SingleAsync());
        await campaigns.CancelAsync(owner.UserId, pilot.Id);

        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.VerticalVideoLibraryV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
        Assert.False(await campaigns.IsKlingFullTourEnabledAsync(owner.UserId));
        // A pilot request with the switch off is refused, never quietly turned into the control edit.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => campaigns.EnqueueAsync(
            owner.UserId,
            owner.PropertyId,
            CampaignOutputSelection.FacebookFeed,
            CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default,
            CampaignVideoStrategy.Kling4FullTour));
        Assert.Contains("switched off", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KlingNativePresenterFailsClearlyInsteadOfSilentlyRenderingEstablishedVideoWhenSwitchTurnsOff()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-native-no-fallback");
        await GrantReviewAdminAsync(owner.UserId);
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using (var portrait = new MemoryStream(OnePixelPng))
            {
                portraitId = (await portraits.UploadAsync(
                    owner.UserId,
                    new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
            }

            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
        };

        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
            await features.ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: true,
                enabledForAllOrganizations: true, // live: the single shared vertical video is live-only
                [owner.OrganizationId]);
            queued = (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4NativePresenter))!;
            await features.ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: false,
                enabledForAllOrganizations: false,
                []);
        }

        var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

        Assert.Equal(CampaignVideoStrategy.Kling4NativePresenter, failed.VideoStrategy);
        Assert.Contains("release switch was turned off", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            failed.Deliverables,
            candidate => candidate.Status == CampaignDeliverableStatus.Rendered);
    }

    [Fact]
    public async Task APilotWorkspaceWithoutTheLibraryNeverSendsAThirtySecond1080pKlingCall()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-pilot-no-library");
        await GrantReviewAdminAsync(owner.UserId);
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using (var portrait = new MemoryStream(OnePixelPng))
            {
                portraitId = (await portraits.UploadAsync(
                    owner.UserId,
                    new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
            }

            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
        }

        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
        };
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        await features.ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4NativePresenterV1,
            globallyEnabled: true,
            enabledForAllOrganizations: false,
            [owner.OrganizationId]);
        try
        {
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            var preview = await campaigns.PreviewKlingNativePresenterAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                null,
                usesVerticalLibrary: false);
            Assert.NotNull(preview);
            Assert.Contains(NativePresenterVideoPolicy.PilotNeedsLibraryMessage, preview.Problems);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => campaigns.EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4NativePresenter));
            Assert.Equal(NativePresenterVideoPolicy.PilotNeedsLibraryMessage, exception.Message);
        }
        finally
        {
            await features.ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: false,
                enabledForAllOrganizations: false,
                []);
        }
    }

    [Fact]
    public async Task AKlingPilotLibraryCampaignFailsInsteadOfFallingBackWhenTheLibraryIsSwitchedOffAfterQueuing()
    {
        var (admin, owner) = await CreateLibraryListingAsync("kling4-pilot-library-off-after-queue");
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services,
            owner.UserId,
            Domain.Billing.SubscriptionPlan.Professional,
            fixture.Factory.TimeProvider.GetUtcNow());
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using var portrait = new MemoryStream(OnePixelPng);
            portraitId = (await portraits.UploadAsync(
                owner.UserId,
                new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
        }

        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            await using (var featureScope = fixture.Factory.Services.CreateAsyncScope())
            {
                await featureScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                    admin,
                    FeatureFlagKeys.Kling4NativePresenterV1,
                    globallyEnabled: true,
                    enabledForAllOrganizations: false,
                    [owner.OrganizationId]);
            }

            QueueLibraryProviders(narratedVideos: 0);
            var options = CampaignCreativeOptions.SocialDefault with
            {
                Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
                AgentPortraitId = portraitId,
                AgentAvatarConsentConfirmed = true,
            };
            CampaignGenerationResult queued;
            await using (var enqueueScope = fixture.Factory.Services.CreateAsyncScope())
            {
                queued = (await enqueueScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                    owner.UserId,
                    owner.PropertyId,
                    CampaignOutputSelection.TikTok,
                    options,
                    CampaignNarrationPreferences.Default,
                    CampaignVideoStrategy.Kling4NativePresenter,
                    new VerticalLibraryRequest(
                        VerticalVideoSelection.RecommendedThree,
                        VerticalCampaignGoal.MaximizeReach)))!;
            }

            // The library is switched off while the campaign waits in the queue; the Kling pilot stays on.
            await SetLibrarySwitchAsync(admin, false, false);
            var submissionsBefore = fixture.Factory.WalkthroughVideoProvider.NativePresenterSubmissions
                .Count(submission => submission.IdempotencyKey.Contains(queued.Id.ToString("N"), StringComparison.Ordinal));

            var failed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Failed);

            Assert.True(failed.LastError?.Contains(NativePresenterVideoPolicy.PilotNeedsLibraryMessage, StringComparison.Ordinal) == true, $"{failed.CurrentStage}: {failed.LastError}");
            Assert.Contains("No Kling video was made.", failed.LastError, StringComparison.Ordinal);
            Assert.Equal(
                submissionsBefore,
                fixture.Factory.WalkthroughVideoProvider.NativePresenterSubmissions
                    .Count(submission => submission.IdempotencyKey.Contains(queued.Id.ToString("N"), StringComparison.Ordinal)));
            await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
            var job = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CampaignGenerationJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == queued.Id);
            Assert.Null(job.FullTourProviderRequestId);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
            await using var featureScope = fixture.Factory.Services.CreateAsyncScope();
            await featureScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                admin,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: false,
                enabledForAllOrganizations: false,
                []);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("refused")]
    [InlineData("busy")]
    [InlineData("download")]
    [InlineData("live")]
    public async Task KlingNativePresenterMakesOneKlingVideoPerLibraryVideoWithItsOwnLengthAndScript(string providerProblem)
    {
        // "live": the switch is on for every workspace. Otherwise the workspace is on the pilot list.
        var live = providerProblem == "live";
        var failOneKlingVideo = providerProblem == "refused";
        var (admin, owner) = await CreateLibraryListingAsync($"kling4-native-library-{providerProblem}");
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services,
            owner.UserId,
            Domain.Billing.SubscriptionPlan.Professional,
            fixture.Factory.TimeProvider.GetUtcNow());
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using var portrait = new MemoryStream(OnePixelPng);
            portraitId = (await portraits.UploadAsync(
                owner.UserId,
                new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
        }

        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            await using (var featureScope = fixture.Factory.Services.CreateAsyncScope())
            {
                await featureScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                    admin,
                    FeatureFlagKeys.Kling4NativePresenterV1,
                    globallyEnabled: true,
                    enabledForAllOrganizations: live,
                    live ? [] : [owner.OrganizationId]);
            }

            QueueLibraryProviders(narratedVideos: 0);
            var options = CampaignCreativeOptions.SocialDefault with
            {
                Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
                AgentPortraitId = portraitId,
                AgentAvatarConsentConfirmed = true,
                // The agent's own words for the Price first video; the others get a script written for their format.
                FormatPresenterScripts =
                [
                    new FormatPresenterScript(
                        VerticalCreativeFormat.PriceFirst,
                        new NativePresenterScript("Hi, I'm Dana.", "Here is the price and the kitchen.", "Call me today.")),
                ],
            };
            CampaignGenerationResult queued;
            await using (var enqueueScope = fixture.Factory.Services.CreateAsyncScope())
            {
                queued = (await enqueueScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                    owner.UserId,
                    owner.PropertyId,
                    CampaignOutputSelection.TikTok,
                    options,
                    CampaignNarrationPreferences.Default,
                    CampaignVideoStrategy.Kling4NativePresenter,
                    new VerticalLibraryRequest(
                        VerticalVideoSelection.RecommendedThree,
                        VerticalCampaignGoal.MaximizeReach)))!;
            }

            if (providerProblem is "refused" or "busy")
            {
                // Refused: the first video fails on its own and the others still get theirs. Busy: Higgsfield is still
                // working on another generation, so the video waits and is submitted again instead of failing.
                await ProcessUntilStageAsync(owner, queued.Id, CampaignGenerationStage.GenerateRequiredAiVideo);
                fixture.Factory.WalkthroughVideoProvider.EnqueueSubmissionFailure(failOneKlingVideo
                    ? new InvalidOperationException("Kling rejected this request.")
                    : new HttpRequestException("Busy.", null, System.Net.HttpStatusCode.TooManyRequests));
            }

            if (providerProblem == "download")
            {
                // Kling finished the video but the download failed: the retry downloads the same video again and
                // never submits (or pays for) a second one.
                await ProcessUntilStageAsync(owner, queued.Id, CampaignGenerationStage.GenerateRequiredAiVideo);
                for (var attempt = 0; attempt < ICampaignGenerationProcessor.MaximumStageAttempts; attempt++)
                {
                    fixture.Factory.WalkthroughVideoProvider.EnqueueKlingPollFailure(new FinishedVideoDownloadException(
                        "Higgsfield",
                        new HttpRequestException("Forbidden.", null, System.Net.HttpStatusCode.Forbidden)));
                }
            }

            await ProcessUntilStageAsync(owner, queued.Id, CampaignGenerationStage.RenderHero);
            Assert.False(fixture.Factory.WalkthroughVideoProvider.HasPendingKlingPollFailures);

            await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
            var campaigns = verifyScope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            var result = await campaigns.GetAsync(owner.UserId, queued.Id);
            Assert.NotNull(result);
            Assert.NotEqual(CampaignGenerationStatus.Failed, result.Status);
            Assert.Equal(CampaignGenerationStage.RenderHero, result.CurrentStage);
            Assert.Equal(CampaignVideoStrategy.Kling4NativePresenter, result.VideoStrategy);
            Assert.Equal(
                [VerticalCreativeFormat.PriceFirst],
                result.CreativeOptions.FormatPresenterScripts?.Select(item => item.Format));

            // One Kling call per library video, never one shared video.
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await database.CampaignGenerationJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == queued.Id);
            Assert.Null(job.FullTourProviderRequestId);
            var variants = await database.CampaignVideoVariants.AsNoTracking()
                .Where(candidate => candidate.Library.CampaignGenerationJobId == queued.Id && candidate.IsEligible)
                .OrderBy(candidate => candidate.Sequence)
                .ToArrayAsync();
            var kling = variants.Where(candidate => candidate.DurationSeconds <= 30).ToArray();
            Assert.True(kling.Length >= 2, $"Expected several Kling videos, got {kling.Length}.");
            if (providerProblem == "download")
            {
                // Out of retries, the video is marked failed but keeps its paid Kling request, so a later retry
                // downloads it instead of submitting a new one.
                var undownloaded = kling[0];
                Assert.Equal(CampaignVideoVariantStatus.Failed, undownloaded.Status);
                Assert.Contains("finished the video, but it could not be downloaded (HTTP 403)", undownloaded.Error, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(undownloaded.KlingProviderRequestId));
                Assert.Null(undownloaded.KlingAssetPath);
                kling = kling[1..];
            }

            if (failOneKlingVideo)
            {
                var refused = kling[0];
                Assert.Equal(CampaignVideoVariantStatus.Failed, refused.Status);
                Assert.Equal("Kling rejected this request.", refused.Error);
                Assert.Null(refused.KlingProviderRequestId);
                kling = kling[1..];
            }

            Assert.All(kling, variant =>
            {
                Assert.False(string.IsNullOrWhiteSpace(variant.KlingProviderRequestId));
                Assert.EndsWith($"/{variant.DurationSeconds}s", variant.KlingGenerationVersion, StringComparison.Ordinal);
                Assert.Equal(CampaignGenerationProcessor.VariantKlingAssetPath(
                    owner.OrganizationId, owner.PropertyId, queued.Id, variant.CampaignVideoLibraryId, variant.Id), variant.KlingAssetPath);
                Assert.Equal(variant.DurationSeconds * 1_000, variant.KlingDurationMs);
            });
            Assert.Equal(kling.Length, kling.Select(variant => variant.KlingProviderRequestId).Distinct().Count());
            Assert.All(variants.Where(candidate => candidate.DurationSeconds > 30), variant => Assert.Null(variant.KlingProviderRequestId));

            var sent = fixture.Factory.WalkthroughVideoProvider.NativePresenterSubmissions
                .Where(submission => submission.IdempotencyKey.Contains(queued.Id.ToString("N"), StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(kling.Length + (providerProblem == "download" ? 1 : 0), sent.Length);
            // Higgsfield takes one generation at a time, so each video waits for the one before it.
            Assert.DoesNotContain(
                fixture.Factory.WalkthroughVideoProvider.OverlappingNativePresenterSubmissions,
                key => key.Contains(queued.Id.ToString("N"), StringComparison.Ordinal));
            foreach (var variant in kling)
            {
                var submission = Assert.Single(sent, item => item.IdempotencyKey.EndsWith(variant.Id.ToString("N"), StringComparison.Ordinal));
                Assert.Equal(variant.DurationSeconds, submission.DurationSeconds);
                Assert.InRange(
                    NativePresenterScript.CountWords(submission.OpeningLine)
                        + NativePresenterScript.CountWords(submission.TourVoiceover)
                        + NativePresenterScript.CountWords(submission.ClosingLine),
                    1,
                    NativePresenterScript.MaximumWordsFor(variant.DurationSeconds));
                if (variant.Format == VerticalCreativeFormat.PriceFirst)
                {
                    Assert.Equal("Here is the price and the kitchen.", submission.TourVoiceover);
                }

                // Pilot: every video is a 15-second 720p Kling video. Live: its full length at the configured 1080p.
                Assert.Equal(live ? null : NativePresenterVideoPolicy.PilotResolution, submission.Resolution);
                if (!live)
                {
                    Assert.Equal(NativePresenterVideoPolicy.PilotDurationSeconds, variant.DurationSeconds);
                }

                // The exact request is kept with the real uploaded image URLs, for the admin audit.
                Assert.NotNull(variant.KlingSubmittedRequestJson);
                Assert.Contains("https://fake-uploads.test/agent.jpg", variant.KlingSubmittedRequestJson, StringComparison.Ordinal);
                Assert.Contains("https://fake-uploads.test/photo-1.jpg", variant.KlingSubmittedRequestJson, StringComparison.Ordinal);
            }

            Assert.Contains(variants, variant => variant.Format == VerticalCreativeFormat.WowFeature);
            if (live)
            {
                // Live keeps each recipe's own length: the 15-second Wow feature and 30-second videos.
                Assert.Contains(kling, variant => variant.DurationSeconds == 30);
            }
            else
            {
                // Pilot makes every library video, of any length, with its own 15-second Kling call.
                Assert.All(variants, variant => Assert.Equal(NativePresenterVideoPolicy.PilotDurationSeconds, variant.DurationSeconds));
            }
            Assert.True(await campaigns.CancelAsync(owner.UserId, queued.Id));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
            await using var featureScope = fixture.Factory.Services.CreateAsyncScope();
            await featureScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                admin,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: false,
                enabledForAllOrganizations: false,
                []);
        }
    }

    [Fact]
    public async Task KlingFullTourIsGeneratedOnceAndSuppliedOnlyToTheThirtySecondRender()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-render");
        await GrantReviewAdminAsync(owner.UserId);
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
            await using var entry = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "entry.png", "image/png", entry.Length, entry, "Entry foyer"));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
        {
            Category = PropertyMediaCategory.Entry,
            RoomType = "Entry foyer",
        });
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);

        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4FullTourV1,
                globallyEnabled: true,
                enabledForAllOrganizations: false,
                [owner.OrganizationId]);
            queued = (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                CampaignCreativeOptions.SocialDefault,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4FullTour))!;
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(CampaignVideoStrategy.Kling4FullTour, completed.VideoStrategy);
        Assert.Equal(1, fixture.Factory.VideoRenderer.FullTourInputCount);
        Assert.NotNull(fixture.Factory.VideoRenderer.LastRequest?.FullTour);
        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CampaignGenerationJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == queued.Id);
        Assert.NotNull(stored.FullTourAssetPath);
        Assert.Equal(30_000, stored.FullTourDurationMs);

        await verifyScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4FullTourV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
    }

    [Fact]
    public async Task KlingNativePresenterUsesConsentedPortraitAndPreservesProviderAudio()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-native-presenter");
        await GrantReviewAdminAsync(owner.UserId);
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using (var portrait = new MemoryStream(OnePixelPng))
            {
                portraitId = (await portraits.UploadAsync(
                    owner.UserId,
                    new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
            }

            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
            await using var kitchen = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "kitchen.png", "image/png", kitchen.Length, kitchen, "Kitchen"));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
        {
            Category = PropertyMediaCategory.Kitchen,
            RoomType = "Kitchen",
            Description = "A center island anchors the kitchen.",
        });
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
            PresenterScript = new NativePresenterScript(
                "Hi, I'm Dana. Come see this home with me.",
                "Step inside to the bright entry,   then the kitchen with its center island.",
                "Call me today to book a private showing."),
        };

        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: true,
                enabledForAllOrganizations: true, // live: the single shared vertical video is live-only
                [owner.OrganizationId]);
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            var preview = await campaigns.PreviewKlingNativePresenterAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                null,
                usesVerticalLibrary: false,
                siteBaseUri: new Uri("https://creativelistingstudio.test/"));
            Assert.NotNull(preview);
            Assert.True(preview.IsReady, string.Join("; ", preview.Problems));
            Assert.Equal(["Exterior front", "Kitchen"], preview.Frames.Select(frame => frame.StopName));
            Assert.Contains("the kitchen with its center island.", preview.Prompt, StringComparison.Ordinal);
            Assert.Equal("Hi, I'm Dana. Come see this home with me.", preview.Script.Opening);
            // Every parameter that would be sent, with each photo named in its image slot until it is uploaded.
            var request = Assert.Single(preview.Videos!).Request;
            Assert.NotNull(request);
            Assert.Equal("POST", request.Method);
            using (var body = System.Text.Json.JsonDocument.Parse(request.BodyJson))
            {
                var root = body.RootElement;
                // Until Generate uploads them, each image slot links this site's copy of the same photo.
                Assert.Equal($"https://creativelistingstudio.test/agent-portraits/{portraitId}", root.GetProperty("image_url").GetString());
                Assert.False(root.TryGetProperty("end_image_url", out _));
                Assert.Equal(
                    preview.Frames.Select(frame => $"https://creativelistingstudio.test/property-media/{frame.PropertyMediaId}"),
                    root.GetProperty("image_urls").EnumerateArray().Select(item => item.GetString()));
                Assert.Equal(preview.Prompt, root.GetProperty("prompt").GetString());
                Assert.Equal(30, root.GetProperty("duration").GetInt32());
            }

            Assert.Equal(portraitId, preview.Videos![0].AgentPortraitId);
            var landscapeOnly = await campaigns.PreviewKlingNativePresenterAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.FacebookFeed,
                options,
                null,
                usesVerticalLibrary: false);
            Assert.Contains(landscapeOnly!.Problems, problem => problem.Contains("TikTok", StringComparison.Ordinal));
            var noVertical = await Assert.ThrowsAsync<InvalidOperationException>(() => campaigns.EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.FacebookFeed,
                options,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4NativePresenter));
            Assert.Contains("30-second vertical", noVertical.Message, StringComparison.Ordinal);
            queued = (await campaigns.EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4NativePresenter))!;
        }

        var completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

        Assert.Equal(CampaignVideoStrategy.Kling4NativePresenter, completed.VideoStrategy);
        // The agent's exact script is stored with the campaign and sent to Kling word for word (whitespace tidied).
        Assert.Equal(
            "Step inside to the bright entry, then the kitchen with its center island.",
            completed.CreativeOptions.PresenterScript?.Tour);
        var sent = Assert.IsType<NativePresenterVideoSubmission>(fixture.Factory.WalkthroughVideoProvider.LastNativePresenterSubmission);
        Assert.Equal("Hi, I'm Dana. Come see this home with me.", sent.OpeningLine);
        Assert.Equal("Step inside to the bright entry, then the kitchen with its center island.", sent.TourVoiceover);
        Assert.Equal("Call me today to book a private showing.", sent.ClosingLine);
        Assert.Equal("Higgsfield", completed.ProviderAudit?.Provider);
        Assert.False(string.IsNullOrWhiteSpace(completed.ProviderAudit?.RequestId));
        Assert.Equal("fake-kling4-native-presenter-v1", completed.ProviderAudit?.GenerationVersion);
        var render = Assert.IsType<VideoRenderRequest>(fixture.Factory.VideoRenderer.LastRequest);
        Assert.True(render.FullTour?.UseNativeAudio);
        Assert.Null(render.Narration);
        Assert.Null(render.Music);
        Assert.Null(render.AgentPresenter);
        Assert.Empty(render.Specification.Audio.NarrationSegments);

        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        await verifyScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4NativePresenterV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
    }

    [Fact]
    public async Task KlingNativePresenterWithoutProviderAudioKeepsThePictureAndAddsCampaignVoice()
    {
        var owner = await CreateOwnerAndPropertyAsync("kling4-native-silent");
        await GrantReviewAdminAsync(owner.UserId);
        Guid portraitId;
        await using (var uploadScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var portraits = uploadScope.ServiceProvider.GetRequiredService<IAgentPortraitService>();
            await using (var portrait = new MemoryStream(OnePixelPng))
            {
                portraitId = (await portraits.UploadAsync(
                    owner.UserId,
                    new AgentPortraitUpload("agent.png", "image/png", portrait.Length, portrait))).Id;
            }

            var media = uploadScope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var exterior = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "exterior.png", "image/png", exterior.Length, exterior, WalkthroughStoryboard.ExteriorFront));
            await using var kitchen = new MemoryStream(OnePixelPng);
            await media.UploadAsync(owner.UserId, owner.PropertyId, new PropertyMediaUpload(
                "kitchen.png", "image/png", kitchen.Length, kitchen, "Kitchen"));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis);
        fixture.Factory.MediaAnalyzer.Enqueue(SuccessfulAnalysis with
        {
            Category = PropertyMediaCategory.Kitchen,
            RoomType = "Kitchen",
            Description = "A center island anchors the kitchen.",
        });
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
            AgentAvatarConsentConfirmed = true,
        };

        CampaignGenerationResult queued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
                owner.UserId,
                FeatureFlagKeys.Kling4NativePresenterV1,
                globallyEnabled: true,
                enabledForAllOrganizations: true, // live: the single shared vertical video is live-only
                [owner.OrganizationId]);
            var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
            queued = (await campaigns.EnqueueAsync(
                owner.UserId,
                owner.PropertyId,
                CampaignOutputSelection.TikTok,
                options,
                CampaignNarrationPreferences.Default,
                CampaignVideoStrategy.Kling4NativePresenter))!;
        }

        fixture.Factory.RenderedVideoValidator.SourceAudioPresent = false;
        CampaignGenerationResult completed;
        try
        {
            completed = await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        }
        finally
        {
            fixture.Factory.RenderedVideoValidator.SourceAudioPresent = null;
        }

        Assert.Equal(CampaignVideoStrategy.Kling4NativePresenter, completed.VideoStrategy);
        Assert.Equal("Higgsfield", completed.ProviderAudit?.Provider);
        Assert.False(string.IsNullOrWhiteSpace(completed.ProviderAudit?.RequestId));
        Assert.Equal("fake-kling4-native-presenter-v1", completed.ProviderAudit?.GenerationVersion);
        var render = Assert.IsType<VideoRenderRequest>(fixture.Factory.VideoRenderer.LastRequest);
        Assert.NotNull(render.FullTour);
        Assert.False(render.FullTour.UseNativeAudio);
        Assert.NotNull(render.Narration);
        Assert.Null(render.AgentPresenter);

        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        await verifyScope.ServiceProvider.GetRequiredService<IFeatureStateService>().ConfigureAsync(
            owner.UserId,
            FeatureFlagKeys.Kling4NativePresenterV1,
            globallyEnabled: false,
            enabledForAllOrganizations: false,
            []);
    }

}
