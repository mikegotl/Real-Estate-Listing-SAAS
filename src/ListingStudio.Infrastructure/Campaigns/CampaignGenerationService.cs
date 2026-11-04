using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Properties;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class CampaignGenerationService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage assetStorage,
    TimeProvider timeProvider,
    ICampaignBillingGate campaignBillingGate,
    IPlanFeatureGate planFeatureGate,
    IWalkthroughTransitionService walkthroughTransitionService,
    IFeatureStateService featureStateService,
    IVerticalCreativePlanningService verticalPlanningService,
    IWalkthroughVideoProvider walkthroughVideoProvider,
    UserManager<ApplicationUser> users) : ICampaignGenerationService, ICampaignTestGenerationService
{
    private const string WorkflowVersion = "campaign-localization-v9";

    public Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(userId, propertyId, CampaignOutputSelection.LegacyStandard, cancellationToken);

    public async Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CancellationToken cancellationToken = default)
        => await EnqueueAsync(
            userId,
            propertyId,
            requestedOutputs,
            CampaignCreativeOptions.Legacy,
            CampaignNarrationPreferences.Default,
            cancellationToken);

    public async Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CancellationToken cancellationToken = default) =>
        await EnqueueAsync(
            userId,
            propertyId,
            requestedOutputs,
            creativeOptions,
            CampaignNarrationPreferences.Default,
            cancellationToken);

    public Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(userId, propertyId, requestedOutputs, creativeOptions, narrationPreferences, null, cancellationToken);

    public Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        VerticalVideoSelection verticalSelection,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(verticalSelection))
        {
            throw new ArgumentOutOfRangeException(nameof(verticalSelection));
        }

        return EnqueueCoreAsync(
            userId, propertyId, requestedOutputs, creativeOptions, narrationPreferences, null, cancellationToken,
            new VerticalLibraryRequest(verticalSelection));
    }

    public Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        VerticalLibraryRequest verticalRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verticalRequest);
        verticalRequest.Validate();
        return EnqueueCoreAsync(
            userId, propertyId, requestedOutputs, creativeOptions, narrationPreferences, null, cancellationToken, verticalRequest);
    }

    public Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        CampaignVideoStrategy videoStrategy,
        VerticalLibraryRequest? verticalRequest = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(videoStrategy))
        {
            throw new ArgumentOutOfRangeException(nameof(videoStrategy));
        }

        verticalRequest?.Validate();
        return EnqueueCoreAsync(
            userId,
            propertyId,
            requestedOutputs,
            creativeOptions,
            narrationPreferences,
            null,
            cancellationToken,
            verticalRequest,
            videoStrategy: videoStrategy);
    }

    /// <summary>Whether the request makes a 30-second vertical video, the only output the native presenter replaces.</summary>
    public static bool HasThirtySecondVertical(
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions) =>
        (requestedOutputs.HasFlag(CampaignOutputSelection.TikTok)
            || requestedOutputs.HasFlag(CampaignOutputSelection.InstagramReel)
            || requestedOutputs.HasFlag(CampaignOutputSelection.YouTubeShort))
        && ShortFormCopyBuilder.DurationFor(creativeOptions.Voiceover) == RequestedDuration.Feature30;

    public async Task<bool> IsVerticalLibraryEnabledAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, organizationId, cancellationToken);
    }

    public async Task<bool> IsKlingFullTourEnabledAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await featureStateService.IsEnabledAsync(FeatureFlagKeys.Kling4FullTourV1, organizationId, cancellationToken);
    }

    public async Task<bool> IsKlingNativePresenterEnabledAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await featureStateService.IsEnabledAsync(
            FeatureFlagKeys.Kling4NativePresenterV1,
            organizationId,
            cancellationToken);
    }

    public async Task<KlingNativePresenterPreview?> PreviewKlingNativePresenterAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        VerticalCampaignGoal? goal,
        bool usesVerticalLibrary,
        IReadOnlyList<VerticalCreativeFormat>? libraryFormats = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creativeOptions);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId)
            .Select(candidate => new { candidate.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            return null;
        }

        // The same checks, in the same words, that queueing and the Worker apply.
        var problems = new List<string>();
        if (!await featureStateService.IsEnabledAsync(FeatureFlagKeys.Kling4NativePresenterV1, organizationId, cancellationToken))
        {
            problems.Add("The Kling4NativePresenterV1 release switch is off for this workspace (Admin, Release switches).");
        }

        if (!walkthroughVideoProvider.IsEnabled)
        {
            problems.Add("Higgsfield is not configured on this site, so Kling cannot be called.");
        }

        if (!(await planFeatureGate.GetAccessAsync(organizationId, userId, cancellationToken)).IncludesPremiumAi)
        {
            problems.Add(PlanFeatureAccess.PremiumAiUpgradeMessage);
        }

        var ownsPortrait = creativeOptions.AgentPortraitId is { } portraitId
            && await dbContext.AgentPortraits.AsNoTracking().AnyAsync(
                candidate => candidate.Id == portraitId
                    && candidate.OrganizationId == organizationId
                    && candidate.UserId == userId,
                cancellationToken);
        if (!ownsPortrait)
        {
            problems.Add("Add your own profile headshot; Kling opens and closes on it.");
        }

        if (!creativeOptions.Elements.HasFlag(CampaignVideoElement.AgentAvatar) || !creativeOptions.AgentAvatarConsentConfirmed)
        {
            problems.Add("Tick the AI presenter consent box under Agent presenter.");
        }

        if (!usesVerticalLibrary && !HasThirtySecondVertical(requestedOutputs, creativeOptions))
        {
            problems.Add("Select TikTok with a short or no voiceover; Kling only replaces the 30-second vertical video.");
        }

        // One request per library video of 30 seconds or less, exactly as the Worker would plan them; otherwise the
        // one shared 30-second vertical video.
        var requests = new List<(VerticalCreativeFormat? Format, int Seconds, Guid? Lead, NativePresenterScript? Script)>();
        if (usesVerticalLibrary && libraryFormats is { Count: > 0 })
        {
            var verticalPlan = await verticalPlanningService.PlanForOrganizationAsync(
                organizationId, propertyId, VerticalRecommendationContext.From(creativeOptions), cancellationToken);
            foreach (var format in libraryFormats.Distinct())
            {
                var recipe = verticalPlan?.Recipes.FirstOrDefault(candidate => candidate.Format == format);
                var seconds = recipe?.DurationSeconds ?? NativePresenterVideoPolicy.DefaultDurationSeconds;
                if (seconds > NativePresenterVideoPolicy.MaximumDurationSeconds)
                {
                    continue;
                }

                requests.Add((format, seconds, recipe?.PrimaryPropertyMediaId,
                    creativeOptions.FormatPresenterScripts?.FirstOrDefault(item => item.Format == format)?.Script));
            }

            if (requests.Count == 0)
            {
                problems.Add("None of the chosen vertical videos is 30 seconds or less, so Kling would not be used.");
            }
        }
        else
        {
            requests.Add((null, NativePresenterVideoPolicy.DefaultDurationSeconds, null, creativeOptions.PresenterScript));
        }

        var videos = new List<KlingNativePresenterVideoPreview>(requests.Count);
        foreach (var request in requests)
        {
            var videoProblems = new List<string>();
            var plan = await NativePresenterRequestPlanner.BuildAsync(
                dbContext,
                new NativePresenterPlanInput(
                    organizationId,
                    propertyId,
                    goal,
                    creativeOptions.MarketingTemplate,
                    creativeOptions.OpenHouseText,
                    creativeOptions.Soundtrack,
                    request.Script,
                    request.Format,
                    request.Seconds,
                    request.Lead),
                cancellationToken);
            if (plan.Frames.Count < NativePresenterVideoPolicy.MinimumTourFrames)
            {
                videoProblems.Add("Place at least one photo on the Tour Map.");
            }

            try
            {
                plan.Script.Validate(request.Seconds);
            }
            catch (ArgumentException exception)
            {
                videoProblems.Add(exception.Message.Split(" (Parameter", 2)[0]);
            }

            string? prompt = null;
            if (plan.Frames.Count > 0)
            {
                try
                {
                    prompt = walkthroughVideoProvider.BuildNativePresenterPrompt(new NativePresenterVideoSubmission(
                        new WalkthroughVideoFrame(Stream.Null, "image/jpeg"),
                        plan.Frames.Select(frame => new NativePresenterTourFrame(
                                new WalkthroughVideoFrame(Stream.Null, frame.MimeType),
                                frame.StopName,
                                frame.ApprovedHighlight,
                                frame.DirectionToNext))
                            .ToArray(),
                        plan.Script.Opening,
                        plan.Script.Closing,
                        plan.CampaignGoal,
                        plan.MusicDirection,
                        "preview",
                        plan.Script.Tour,
                        request.Seconds));
                }
                catch (ArgumentException exception)
                {
                    videoProblems.Add($"The request could not be built: {exception.Message}");
                }
            }

            videos.Add(new KlingNativePresenterVideoPreview(
                request.Format ?? VerticalCreativeFormat.LegacySingle,
                request.Seconds,
                plan.Frames.Select((frame, index) => new KlingNativePresenterPreviewFrame(
                        index + 1,
                        frame.PropertyMediaId,
                        frame.StopName,
                        frame.ApprovedHighlight,
                        frame.DirectionToNext?.ToString()))
                    .ToArray(),
                prompt,
                plan.Script,
                videoProblems));
        }

        // Each video's own problems are listed under its name when there is more than one video.
        foreach (var video in videos)
        {
            problems.AddRange(videos.Count > 1 || video.Format != VerticalCreativeFormat.LegacySingle
                ? video.Problems.Select(problem => $"{VerticalFormatNames.Name(video.Format)}: {problem}")
                : video.Problems);
        }

        var first = videos.FirstOrDefault();
        return new KlingNativePresenterPreview(
            problems.Distinct().ToArray(),
            first?.Frames ?? [],
            first?.Prompt,
            walkthroughVideoProvider.EstimatedNativePresenterCostUsd * videos.Count,
            first?.Script ?? new NativePresenterScript(string.Empty, string.Empty, string.Empty),
            videos);
    }

    public async Task<NativePresenterScript?> SuggestNativePresenterScriptAsync(
        string userId,
        Guid propertyId,
        CampaignMarketingTemplate marketingTemplate,
        string? openHouseText,
        VerticalCampaignGoal? goal,
        VerticalCreativeFormat? format = null,
        int durationSeconds = 30,
        Guid? leadMediaId = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds is < NativePresenterVideoPolicy.MinimumDurationSeconds
            or > NativePresenterVideoPolicy.MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Kling 4 makes 3 to 30 seconds of video per call.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await dbContext.Properties.AsNoTracking().AnyAsync(
                candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
                cancellationToken))
        {
            return null;
        }

        var plan = await NativePresenterRequestPlanner.BuildAsync(
            dbContext,
            new NativePresenterPlanInput(
                organizationId,
                propertyId,
                goal,
                marketingTemplate,
                openHouseText?.Trim(),
                CampaignSoundtrack.AutoByHomeEra,
                null,
                format == VerticalCreativeFormat.LegacySingle ? null : format,
                durationSeconds,
                leadMediaId),
            cancellationToken);
        return plan.Script;
    }

    public Task<CampaignGenerationResult?> EnqueueTestAsync(string userId, Guid propertyId,
        CampaignCreativeOptions options, Guid caseRequestId, CancellationToken cancellationToken = default)
    {
        if (caseRequestId == Guid.Empty) throw new ArgumentException("A test case key is required.", nameof(caseRequestId));
        return EnqueueCoreAsync(userId, propertyId, CampaignOutputSelection.TikTok, options,
            CampaignNarrationPreferences.Default, caseRequestId.ToString("N"), cancellationToken);
    }

    public Task<CampaignGenerationResult?> EnqueueCreativeTestAsync(string userId, Guid propertyId,
        VerticalCreativeFormat format, string hookVariant, Guid caseRequestId,
        CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(captionStyle)) throw new ArgumentOutOfRangeException(nameof(captionStyle));
        if (caseRequestId == Guid.Empty) throw new ArgumentException("A test case key is required.", nameof(caseRequestId));
        if (hookVariant is not ("a" or "b")) throw new ArgumentException("The hook is A or B.", nameof(hookVariant));
        var request = new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen, null, [format]);
        request.Validate();
        return EnqueueCoreAsync(userId, propertyId, CampaignOutputSelection.TikTok, CreativeTestOptions with { CaptionStyle = captionStyle },
            CampaignNarrationPreferences.Default, $"creative:{caseRequestId:N}", cancellationToken, request, hookVariant,
            requireLibrary: true);
    }

    /// <summary>Creative test campaigns: the social defaults without paid AI walk-through clips.</summary>
    public static CampaignCreativeOptions CreativeTestOptions { get; } = CampaignCreativeOptions.SocialDefault with
    {
        Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.WalkthroughMotion,
    };

    private async Task<CampaignGenerationResult?> EnqueueCoreAsync(
        string userId, Guid propertyId, CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions, CampaignNarrationPreferences narrationPreferences,
        string? testCaseKey, CancellationToken cancellationToken,
        VerticalLibraryRequest? verticalRequest = null, string? hookVariant = null, bool requireLibrary = false,
        CampaignVideoStrategy videoStrategy = CampaignVideoStrategy.Established)
    {
        var verticalSelection = verticalRequest?.Selection ?? VerticalVideoSelection.LegacySingle;
        CampaignOutputSelectionPolicy.Validate(requestedOutputs);
        ArgumentNullException.ThrowIfNull(creativeOptions);
        creativeOptions.Validate();
        ArgumentNullException.ThrowIfNull(narrationPreferences);
        narrationPreferences.Validate();
        if (creativeOptions.Elements.HasFlag(CampaignVideoElement.KitchenIslandOrbit)
            && !walkthroughTransitionService.IsEnabled)
        {
            throw new InvalidOperationException(
                "Kitchen island turnaround is unavailable until AI walk-through generation is configured.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var usesKlingFullTour = videoStrategy == CampaignVideoStrategy.Kling4FullTour
            && testCaseKey is null
            && await featureStateService.IsEnabledAsync(
                FeatureFlagKeys.Kling4FullTourV1,
                organizationId,
                cancellationToken);
        var usesKlingNativePresenter = videoStrategy == CampaignVideoStrategy.Kling4NativePresenter
            && testCaseKey is null
            && await featureStateService.IsEnabledAsync(
                FeatureFlagKeys.Kling4NativePresenterV1,
                organizationId,
                cancellationToken);
        // The library is decided here, before anything is queued: with the switch off (globally, or for this
        // workspace) the request is the legacy campaign, with the same fingerprint it always had.
        // Render regression test cases (a test key with no library request) always make the legacy vertical edit.
        var usesLibrary = verticalSelection != VerticalVideoSelection.LegacySingle
            && (testCaseKey is null || requireLibrary)
            && (requestedOutputs.HasFlag(CampaignOutputSelection.TikTok)
                || requestedOutputs.HasFlag(CampaignOutputSelection.InstagramReel)
                || requestedOutputs.HasFlag(CampaignOutputSelection.YouTubeShort))
            && await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, organizationId, cancellationToken);
        if (testCaseKey is null
            && ((videoStrategy == CampaignVideoStrategy.Kling4FullTour && !usesKlingFullTour)
                || (videoStrategy == CampaignVideoStrategy.Kling4NativePresenter && !usesKlingNativePresenter)))
        {
            // Never quietly swap a requested pilot for the control edit: the agent would think they tested Kling.
            throw new InvalidOperationException(
                "The selected Kling 4 pilot is switched off for this workspace. Choose the established edit or ask an admin to turn it on.");
        }

        // The exact presenter script only means something to the native presenter; elsewhere it must not change the
        // campaign's fingerprint or be stored.
        creativeOptions = creativeOptions with
        {
            PresenterScript = usesKlingNativePresenter ? creativeOptions.PresenterScript?.Normalized() : null,
            // Per-video scripts only mean something to a native-presenter library campaign.
            FormatPresenterScripts = usesKlingNativePresenter && usesLibrary && creativeOptions.FormatPresenterScripts is { Count: > 0 } scripts
                ? scripts.Select(item => item with { Script = item.Script.Normalized() }).ToArray()
                : null,
        };

        if (usesKlingNativePresenter && !usesLibrary && !HasThirtySecondVertical(requestedOutputs, creativeOptions))
        {
            throw new InvalidOperationException(
                "The Kling 4 native presenter needs a 30-second vertical video: select TikTok and a short or no voiceover.");
        }

        if (requireLibrary && !usesLibrary)
        {
            throw new InvalidOperationException("Creative format tests are switched off.");
        }

        if (usesLibrary && verticalSelection == VerticalVideoSelection.AgentChosen)
        {
            await EnsureChosenFormatsEligibleAsync(organizationId, propertyId, creativeOptions, verticalRequest!.Formats!, cancellationToken);
        }

        var planAccess = await planFeatureGate.GetAccessAsync(organizationId, userId, cancellationToken);
        if ((usesKlingFullTour || usesKlingNativePresenter) && !planAccess.IncludesPremiumAi)
        {
            throw new PlanFeatureUnavailableException(PlanFeatureAccess.PremiumAiUpgradeMessage);
        }
        if (usesKlingNativePresenter
            && (!creativeOptions.Elements.HasFlag(CampaignVideoElement.AgentAvatar)
                || !creativeOptions.AgentAvatarConsentConfirmed
                || creativeOptions.AgentPortraitId is null))
        {
            throw new InvalidOperationException(
                "The Kling 4 native presenter requires an uploaded profile headshot and AI-presenter consent.");
        }
        if (creativeOptions.Elements.HasFlag(CampaignVideoElement.AgentAvatar) && !planAccess.IncludesPremiumAi)
        {
            throw new PlanFeatureUnavailableException(PlanFeatureAccess.PremiumAiUpgradeMessage);
        }

        DateTimeOffset? portraitUpdatedAtUtc = null;
        if (creativeOptions.AgentPortraitId is { } portraitId)
        {
            portraitUpdatedAtUtc = await dbContext.AgentPortraits.AsNoTracking()
                .Where(candidate => candidate.Id == portraitId
                    && candidate.OrganizationId == organizationId
                    && candidate.UserId == userId)
                .Select(candidate => (DateTimeOffset?)candidate.UpdatedAtUtc)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Upload an agent photo before including it in a campaign.");
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var property = await dbContext.Properties
            .FromSqlInterpolated($"""
                SELECT *
                FROM "Properties"
                WHERE "Id" = {propertyId} AND "OrganizationId" = {organizationId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (property.IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot generate campaigns.");
        }

        var media = await dbContext.PropertyMedia
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .OrderBy(candidate => candidate.DisplayOrder)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.DisplayOrder,
                candidate.UploadedAt,
                candidate.WalkthroughStop,
                candidate.Category,
                candidate.AnalysisDescription,
            })
            .ToArrayAsync(cancellationToken);
        if (media.Length == 0)
        {
            throw new InvalidOperationException("At least one property image is required to generate a campaign.");
        }
        if (usesKlingFullTour && media.Length < 2)
        {
            throw new InvalidOperationException("The Kling 4 full tour needs at least two approved Tour Map photos.");
        }
        if (usesKlingNativePresenter && media.Length < NativePresenterVideoPolicy.MinimumTourFrames)
        {
            throw new InvalidOperationException(
                "The Kling 4 native presenter needs at least one approved Tour Map photo.");
        }
        if (creativeOptions.Elements.HasFlag(CampaignVideoElement.KitchenIslandOrbit))
        {
            var kitchenViews = media.Where(candidate =>
                candidate.Category == Domain.Properties.PropertyMediaCategory.Kitchen
                || string.Equals(candidate.WalkthroughStop, "Kitchen", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kitchenViews.Length < 3
                || !kitchenViews.Any(candidate => candidate.AnalysisDescription?.Contains(
                    "island",
                    StringComparison.OrdinalIgnoreCase) == true))
            {
                throw new InvalidOperationException(
                    "Kitchen island turnaround requires an analyzed island and at least three verified kitchen viewpoints.");
            }
        }

        var neighborhoodFacts = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.IsApproved)
            .OrderBy(candidate => candidate.ProviderPlaceId)
            .Select(candidate => new
            {
                candidate.ProviderPlaceId,
                candidate.Category,
                candidate.Name,
                candidate.Address,
                candidate.DistanceMiles,
                candidate.CheckedAtUtc,
                candidate.VideoPhotoBlobPath,
                candidate.VideoPhotoFileSize,
                candidate.VideoPhotoCredit,
                candidate.VideoPhotoUploadedAtUtc,
            })
            .ToArrayAsync(cancellationToken);
        var userNarration = await UserNarrationSource.LoadAsync(
            dbContext,
            organizationId,
            propertyId,
            cancellationToken);
        var acceptedScript = userNarration is null
            ? null
            : new
            {
                Id = userNarration.SourceId,
                userNarration.IsRoomNarration,
                userNarration.Segments,
                MarketingUseAcceptedAtUtc = userNarration.AcceptedAtUtc,
            };
        if (requestedOutputs.HasFlag(CampaignOutputSelection.FullNarratedTour)
            && userNarration is not { RequiresLongForm: true })
        {
            throw new InvalidOperationException(
                "A full narrated tour requires an accepted narration script longer than 145 words.");
        }
        var walkthroughVideos = await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId
                && video.PropertyId == propertyId
                && video.ProcessingStatus == Domain.Properties.PropertyVideoProcessingStatus.Completed)
            .OrderBy(video => video.TourOrder)
            .ThenBy(video => video.Id)
            .Select(video => new
            {
                video.Id,
                video.TourOrder,
                video.TourStop,
                video.EnhancedBlobPath,
                video.EnhancedDurationMs,
                video.EnhancementVersion,
                video.ProcessingCompletedAtUtc,
            })
            .ToArrayAsync(cancellationToken);
        // Approving, replacing or removing the drone intro changes how the videos open, so it is part of the source.
        // Plans without the drone intro never open with one, so it is left out of their source.
        var aerialIntro = await dbContext.AerialIntroClips
            .AsNoTracking()
            .Where(clip => clip.OrganizationId == organizationId
                && clip.PropertyId == propertyId
                && clip.ApprovedAtUtc != null
                && planAccess.IncludesPremiumAi)
            .Select(clip => new { clip.Id, clip.PropertyMediaId, clip.SourceBlobPath, clip.ApprovedAtUtc, clip.PlaybackRate })
            .SingleOrDefaultAsync(cancellationToken);
        var fingerprint = CreateFingerprint(
            property.UpdatedAtUtc,
            media.Select(candidate => new
            {
                candidate.Id,
                candidate.DisplayOrder,
                candidate.UploadedAt,
            }),
            neighborhoodFacts,
            acceptedScript,
            walkthroughVideos,
            aerialIntro,
            requestedOutputs,
            creativeOptions,
            narrationPreferences,
            portraitUpdatedAtUtc);
        if (usesLibrary)
        {
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                VerticalFingerprintSuffix(fingerprint, verticalRequest!)))).ToLowerInvariant();
        }
        if (usesKlingFullTour)
        {
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{fingerprint}:kling4-full-tour-v1"))).ToLowerInvariant();
        }
        if (usesKlingNativePresenter)
        {
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{fingerprint}:kling4-native-presenter-v1"))).ToLowerInvariant();
        }

        if (testCaseKey is not null)
        {
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"tiktok-test:{testCaseKey}"))).ToLowerInvariant();
        }
        var existing = await dbContext.CampaignGenerationJobs
            .Include(candidate => candidate.Deliverables)
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId
                && candidate.SourceFingerprint == fingerprint
                && (candidate.Status == CampaignGenerationStatus.Queued
                    || candidate.Status == CampaignGenerationStatus.Running
                    || candidate.Status == CampaignGenerationStatus.Completed
                    || testCaseKey != null))
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (testCaseKey is not null && existing.SourceFingerprint != fingerprint) return null;
            return ToResult(existing);
        }

        var conflictingActive = await dbContext.CampaignGenerationJobs.AsNoTracking().AnyAsync(
            candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.SourceFingerprint != fingerprint
                && (candidate.Status == CampaignGenerationStatus.Queued
                    || candidate.Status == CampaignGenerationStatus.Running),
            cancellationToken);
        if (conflictingActive)
        {
            throw new InvalidOperationException(
                "Another campaign for this property is still processing with different settings. Wait for it to finish or cancel it before starting this campaign; your selected video strategy was not changed.");
        }

        await campaignBillingGate.EnsureCampaignCapacityAsync(organizationId, propertyId, userId, cancellationToken);
        var job = CampaignGenerationJob.Create(
            organizationId,
            propertyId,
            userId,
            fingerprint,
            requestedOutputs,
            timeProvider.GetUtcNow(),
            creativeOptions,
            narrationPreferences);
        if (usesKlingFullTour)
        {
            job.RequestVideoStrategy(CampaignVideoStrategy.Kling4FullTour);
        }
        else if (usesKlingNativePresenter)
        {
            job.RequestVideoStrategy(CampaignVideoStrategy.Kling4NativePresenter);
        }
        if (usesLibrary)
        {
            job.RequestVerticalLibrary(
                verticalSelection,
                verticalRequest!.Goal,
                verticalSelection == VerticalVideoSelection.AgentChosen ? verticalRequest.Formats : null,
                hookVariant);
        }

        dbContext.CampaignGenerationJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (creativeOptions.Elements.HasFlag(CampaignVideoElement.KitchenIslandOrbit))
        {
            try
            {
                await walkthroughTransitionService.QueueAsync(userId, propertyId, cancellationToken);
            }
            catch
            {
                dbContext.ChangeTracker.Clear();
                var abandoned = await dbContext.CampaignGenerationJobs.SingleOrDefaultAsync(
                    candidate => candidate.Id == job.Id,
                    CancellationToken.None);
                if (abandoned is not null)
                {
                    dbContext.CampaignGenerationJobs.Remove(abandoned);
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }

                throw;
            }
        }

        return ToResult(job);
    }

    public async Task<CampaignGenerationResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var job = await Query(organizationId)
            .Where(candidate => candidate.PropertyId == propertyId)
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return job is null ? null : ToResult(job);
    }

    public async Task<CampaignGenerationResult?> GetAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var job = await Query(organizationId)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        return job is null ? null : ToResult(job);
    }

    public async Task<bool> RetryAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var job = await dbContext.CampaignGenerationJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (job is null || job.Status != CampaignGenerationStatus.Failed)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        job.Retry(now);
        // Resume only failed library videos, and only while the feature is still on; with it off the worker stops
        // the library at its next stage and keeps the videos that already rendered.
        if (job.UsesVerticalLibrary
            && await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, organizationId, cancellationToken))
        {
            var library = await dbContext.CampaignVideoLibraries
                .Include(candidate => candidate.Variants)
                .SingleOrDefaultAsync(
                    candidate => candidate.CampaignGenerationJobId == job.Id && candidate.OrganizationId == organizationId,
                    cancellationToken);
            if (library is not null && !CampaignVideoLibrary.IsTerminal(library.Status))
            {
                foreach (var variant in library.Variants.Where(variant => variant.Status == CampaignVideoVariantStatus.Failed))
                {
                    variant.Retry(now);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CancelAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var job = await dbContext.CampaignGenerationJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (job is null || job.Status is CampaignGenerationStatus.Completed or CampaignGenerationStatus.Cancelled)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        job.RequestCancellation(now);
        if (job.Status == CampaignGenerationStatus.Cancelled && job.UsesVerticalLibrary)
        {
            var library = await dbContext.CampaignVideoLibraries
                .Include(candidate => candidate.Variants)
                .SingleOrDefaultAsync(
                    candidate => candidate.CampaignGenerationJobId == job.Id
                        && candidate.OrganizationId == organizationId,
                    cancellationToken);
            library?.Cancel(now);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CampaignDownload?> OpenDeliverableAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var deliverable = await dbContext.CampaignDeliverables
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CampaignGenerationJobId == jobId
                && candidate.OrganizationId == organizationId
                && candidate.Kind == kind
                && (candidate.CampaignVideoVariantId == null || candidate.CampaignVideoVariantId == candidate.Id)
                && candidate.Status == CampaignDeliverableStatus.Rendered,
                cancellationToken);
        if (deliverable?.AssetPath is null || deliverable.ContentType is null)
        {
            return null;
        }

        var content = await assetStorage.OpenReadAsync(deliverable.AssetPath, cancellationToken);
        return content is null
            ? null
            : new CampaignDownload(content, deliverable.ContentType, $"listing-studio-{kind.ToString().ToLowerInvariant()}.mp4");
    }

    public async Task<CampaignDownload?> OpenStillPostAsync(
        string userId,
        Guid jobId,
        CampaignStillPostKind kind,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var post = await dbContext.CampaignStillPosts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CampaignGenerationJobId == jobId
                && candidate.OrganizationId == organizationId
                && candidate.Kind == kind
                && candidate.Status == CampaignStillPostStatus.Rendered,
                cancellationToken);
        if (post?.AssetPath is null || post.ContentType is null)
        {
            return null;
        }

        var content = await assetStorage.OpenReadAsync(post.AssetPath, cancellationToken);
        return content is null
            ? null
            : new CampaignDownload(
                content,
                post.ContentType,
                $"listing-studio-{kind.ToString().ToLowerInvariant()}.jpg");
    }

    private IQueryable<CampaignGenerationJob> Query(Guid organizationId) => dbContext.CampaignGenerationJobs
        .AsNoTracking()
        .Include(candidate => candidate.Deliverables)
        .Include(candidate => candidate.StillPosts)
        .Where(candidate => candidate.OrganizationId == organizationId);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static string CreateFingerprint(
        DateTimeOffset propertyUpdatedAtUtc,
        object media,
        object neighborhoodFacts,
        object? acceptedScript,
        object walkthroughVideos,
        object? aerialIntro,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        DateTimeOffset? portraitUpdatedAtUtc)
    {
        var source = JsonSerializer.Serialize(new
        {
            workflowVersion = WorkflowVersion,
            propertyUpdatedAtUtc,
            media,
            neighborhoodFacts,
            acceptedScript,
            walkthroughVideos,
            aerialIntro,
            requestedOutputs,
            creativeOptions,
            narrationPreferences,
            portraitUpdatedAtUtc,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static CampaignGenerationResult ToResult(CampaignGenerationJob job)
    {
        var shownStages = CampaignGenerationStageOrder.For(job.UsesVerticalLibrary, job.CurrentStage);
        var currentIndex = Math.Max(0, shownStages.ToList().IndexOf(job.CurrentStage));
        var stageCount = shownStages.Count;
        var progress = job.Status == CampaignGenerationStatus.Completed
            ? 100
            : currentIndex * 100 / stageCount;
        var stages = shownStages
            .Select(stage => new CampaignStageResult(
                stage,
                StageStatus(job, stage),
                stage == job.CurrentStage ? job.StageAttemptCount : 0))
            .ToArray();
        // Library videos are listed by the library views; this summary keeps the one-per-kind legacy shape.
        var deliverables = job.Deliverables
            .Where(item => item.IsCampaignLevel)
            .OrderBy(item => item.Kind)
            .Select(item => new CampaignDeliverableResult(
                item.Kind,
                item.Status,
                (int)item.RequestedDuration,
                item.AspectRatio == Domain.Videos.VideoAspectRatio.Landscape16By9 ? "16:9" : "9:16",
                item.FileSize,
                item.RenderedAtUtc,
                item.Version))
            .ToArray();
        var stillPosts = job.StillPosts
            .OrderBy(item => item.Kind)
            .Select(item => new CampaignStillPostResult(
                item.Kind,
                item.Status,
                item.Width,
                item.Height,
                item.Caption,
                item.FileSize,
                item.RenderedAtUtc))
            .ToArray();
        return new CampaignGenerationResult(
            job.Id,
            job.PropertyId,
            job.RequestedOutputs,
            job.CreativeOptions,
            new CampaignNarrationPreferences(
                job.NarrationRegion,
                job.SpokenLanguage,
                job.CaptionLanguage,
                job.VoiceAccent,
                job.LocalizeAgentCard),
            job.Status,
            job.CurrentStage,
            progress,
            job.LastError,
            job.CancellationRequested,
            job.CreatedAtUtc,
            job.UpdatedAtUtc,
            job.CompletedAtUtc,
            job.SocialCaptionLong,
            job.SocialCaptionShort,
            stages,
            deliverables,
            stillPosts,
            job.VerticalSelection is { } selection
                ? new CampaignVerticalRequestResult(selection, job.VerticalGoal, job.VerticalFormats)
                : null,
            job.VideoStrategy,
            job.VideoStrategy is CampaignVideoStrategy.Kling4FullTour or CampaignVideoStrategy.Kling4NativePresenter
                ? new CampaignProviderAudit(
                    "Higgsfield",
                    job.FullTourProviderRequestId,
                    job.FullTourGenerationVersion,
                    job.FullTourSubmittedPrompt)
                : null);
    }

    // The goal and an agent's format list are part of what was asked for, so a different choice is a new campaign.
    // A request with neither keeps the Prompt 6 fingerprint.
    private static string VerticalFingerprintSuffix(string fingerprint, VerticalLibraryRequest request)
    {
        var suffix = $"{fingerprint}:vertical-library:{request.Selection}";
        if (request.Goal is { } goal)
        {
            suffix += $":goal:{goal}";
        }

        if (request.Selection == VerticalVideoSelection.AgentChosen)
        {
            suffix += $":formats:{CampaignGenerationJob.FormatVerticalFormats(request.Formats)}";
        }

        return suffix;
    }

    private async Task EnsureChosenFormatsEligibleAsync(
        Guid organizationId,
        Guid propertyId,
        CampaignCreativeOptions creativeOptions,
        IReadOnlyList<VerticalCreativeFormat> formats,
        CancellationToken cancellationToken)
    {
        var plan = await verticalPlanningService.PlanForOrganizationAsync(
            organizationId, propertyId, VerticalRecommendationContext.From(creativeOptions), cancellationToken);
        if (plan is null)
        {
            return;
        }

        // While photo analysis is still running, the photo-led formats may not have scored yet; they are checked
        // again when the library is planned, and dropped there if they still can't be made. Formats that need the
        // agent's own confirmation (a story, an access line, hosted footage) never wait on analysis.
        var analysisPending = plan.Input.Photos.Any(photo => !photo.AnalysisCompleted);
        var ineligible = formats
            .Where(format => !plan.Recommendation.Formats.Any(item => item.Format == format && item.IsEligible))
            .Where(format => !(analysisPending && format is VerticalCreativeFormat.WowFeature or VerticalCreativeFormat.PriceFirst))
            .ToArray();
        if (ineligible.Length > 0)
        {
            throw new InvalidOperationException(
                $"{string.Join(", ", ineligible.Select(VerticalFormatNames.Name))} can't be made for this listing yet. "
                + "Add what it's missing or choose another video.");
        }
    }

    private static CampaignStageStatus StageStatus(
        CampaignGenerationJob job,
        CampaignGenerationStage stage)
    {
        var rank = CampaignGenerationStageOrder.Rank(stage);
        var currentRank = CampaignGenerationStageOrder.Rank(job.CurrentStage);
        if (rank < currentRank)
        {
            return CampaignStageStatus.Completed;
        }

        if (rank > currentRank)
        {
            return CampaignStageStatus.Pending;
        }

        return job.Status switch
        {
            CampaignGenerationStatus.Running => CampaignStageStatus.Running,
            CampaignGenerationStatus.Failed => CampaignStageStatus.Failed,
            CampaignGenerationStatus.Cancelled => CampaignStageStatus.Cancelled,
            CampaignGenerationStatus.Completed => CampaignStageStatus.Completed,
            _ => CampaignStageStatus.Pending,
        };
    }
}

