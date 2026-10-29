using System.Data;
using System.Text.Json;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Notifications;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed partial class CampaignGenerationProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaAnalysisProcessor mediaAnalysisProcessor,
    IPropertyStoryService storyService,
    IVideoProductionPlanService productionPlanService,
    IVideoNarrationService narrationService,
    ICampaignLocalizationService localizationService,
    IAiUsagePropertyContext aiUsagePropertyContext,
    ICampaignDerivativeGenerator derivativeGenerator,
    IGeneratedVideoClipService generatedVideoClipService,
    IAgentAvatarVideoService agentAvatarVideoService,
    IVideoRenderer videoRenderer,
    ICampaignSoundtrackService campaignSoundtrackService,
    IWalkthroughVideoProvider walkthroughVideoProvider,
    IPhotoImageCodec photoImageCodec,
    IPropertyMediaStorage propertyMediaStorage,
    IPropertyVideoClipFinder videoClipFinder,
    ICampaignAssetStorage campaignAssetStorage,
    ICampaignBillingGate campaignBillingGate,
    IOptions<CampaignGenerationOptions> options,
    TimeProvider timeProvider,
    ILogger<CampaignGenerationProcessor> logger) : ICampaignGenerationProcessor
{
    private static readonly JsonSerializerOptions SerializerOptions = VideoSpecificationJson.Options;
    private static readonly JsonSerializerOptions AudioSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<CampaignStageRunResult?> ProcessNextStageAsync(
        CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimNextAsync(cancellationToken);
        if (claimed is null)
        {
            return null;
        }

        var (jobId, stage, attemptNumber, stageTimeoutSeconds) = claimed.Value;
        using var stageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stageTimeout.CancelAfter(TimeSpan.FromSeconds(stageTimeoutSeconds));
        try
        {
            var completed = await ExecuteStageAsync(jobId, stage, stageTimeout.Token);
            dbContext.ChangeTracker.Clear();
            await using var completionTransaction = stage == CampaignGenerationStage.FinalizeCampaign
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            var job = await dbContext.CampaignGenerationJobs
                .Include(candidate => candidate.Deliverables)
                .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (job.CancellationRequested)
            {
                job.Cancel(now);
            }
            else if (!completed)
            {
                job.ContinueStage(now, TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
            }
            else if (stage == CampaignGenerationStage.FinalizeCampaign)
            {
                job.Complete(now);
                await campaignBillingGate.RecordCompletedCampaignAsync(
                    job.OrganizationId,
                    job.Id,
                    cancellationToken);
                await NotifyAsync(job, JobNotificationText.CampaignCompleted, now, cancellationToken);
            }
            else
            {
                job.CompleteStage(Next(stage), now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (completionTransaction is not null)
            {
                await completionTransaction.CommitAsync(cancellationToken);
            }
            return new CampaignStageRunResult(jobId, stage, true, completed, false, attemptNumber, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStageFailure(exception, jobId, stage, attemptNumber);
            dbContext.ChangeTracker.Clear();
            var job = await dbContext.CampaignGenerationJobs.SingleAsync(
                candidate => candidate.Id == jobId,
                CancellationToken.None);
            var now = timeProvider.GetUtcNow();
            var error = SanitizeError(exception, stageTimeout.IsCancellationRequested);
            var willRetry = !job.CancellationRequested
                && attemptNumber < ICampaignGenerationProcessor.MaximumStageAttempts
                && IsRetryable(exception, stageTimeout.IsCancellationRequested);
            if (job.CancellationRequested)
            {
                job.Cancel(now);
            }
            else if (willRetry)
            {
                job.ScheduleRetry(error, now, TimeSpan.FromSeconds(Math.Pow(2, attemptNumber)));
            }
            else
            {
                job.Fail(error, now);
                await NotifyAsync(
                    job,
                    property => JobNotificationText.CampaignFailed(property, stage, error),
                    now,
                    CancellationToken.None);
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
            return new CampaignStageRunResult(jobId, stage, false, false, willRetry, attemptNumber, error);
        }
    }

    private async Task<(Guid JobId, CampaignGenerationStage Stage, int AttemptNumber, int StageTimeoutSeconds)?> ClaimNextAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.CampaignGenerationJobs.FromSqlInterpolated($"""
            SELECT *
            FROM "CampaignGenerationJobs"
            WHERE (
                ("Status" = 'Queued' AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now}))
                OR ("Status" = 'Running' AND "LeaseExpiresAtUtc" <= {now})
            )
            ORDER BY "CreatedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var job = candidates.SingleOrDefault();
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (job.CancellationRequested)
        {
            job.Cancel(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var requestedDurationSeconds = job.CurrentStage == CampaignGenerationStage.RenderLongForm
            ? await dbContext.CampaignDeliverables.AsNoTracking()
                .Where(candidate => candidate.CampaignGenerationJobId == job.Id
                    && (candidate.Kind == CampaignOutputKind.LongForm
                        || candidate.Kind == CampaignOutputKind.FullNarratedTour))
                .Select(candidate => (int?)candidate.RequestedDuration)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var stageTimeoutSeconds = CalculateStageTimeoutSeconds(
            options.Value.StageTimeoutSeconds,
            job.CurrentStage,
            requestedDurationSeconds);
        var leaseSeconds = Math.Max(options.Value.LeaseSeconds, stageTimeoutSeconds + 60);
        job.BeginStage(now, TimeSpan.FromSeconds(leaseSeconds));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = (job.Id, job.CurrentStage, job.StageAttemptCount, stageTimeoutSeconds);
        dbContext.ChangeTracker.Clear();
        return result;
    }

    public static int CalculateStageTimeoutSeconds(
        int configuredTimeoutSeconds,
        CampaignGenerationStage stage,
        int? requestedDurationSeconds)
    {
        if (configuredTimeoutSeconds is < 30 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredTimeoutSeconds));
        }

        if (stage != CampaignGenerationStage.RenderLongForm || requestedDurationSeconds is null)
        {
            return configuredTimeoutSeconds;
        }

        if (requestedDurationSeconds is < 15 or > 900)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDurationSeconds));
        }

        return Math.Min(
            3_600,
            Math.Max(configuredTimeoutSeconds, checked((requestedDurationSeconds.Value * 3) + 120)));
    }

    private Task<bool> ExecuteStageAsync(
        Guid jobId,
        CampaignGenerationStage stage,
        CancellationToken cancellationToken) => stage switch
        {
            CampaignGenerationStage.ValidateProperty => ValidatePropertyAsync(jobId, cancellationToken),
            CampaignGenerationStage.AnalyzeMedia => AnalyzeMediaAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateStory => GenerateStoryAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateMasterVideoPlan => GenerateMasterPlanAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateNarration => GenerateNarrationAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateDerivativePlans => GenerateDerivativePlansAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateRequiredAiVideo => GenerateRequiredAiVideoAsync(jobId, cancellationToken),
            CampaignGenerationStage.RenderHero => RenderSelectedAsync(
                jobId,
                [CampaignOutputKind.Hero, CampaignOutputKind.YouTubeTour],
                cancellationToken),
            CampaignGenerationStage.RenderFeature => RenderSelectedAsync(
                jobId,
                [CampaignOutputKind.Feature, CampaignOutputKind.TikTok, CampaignOutputKind.InstagramReel,
                    CampaignOutputKind.YouTubeShort, CampaignOutputKind.FacebookFeed],
                cancellationToken),
            CampaignGenerationStage.RenderTeaser => RenderSelectedAsync(
                jobId,
                [CampaignOutputKind.Teaser],
                cancellationToken),
            CampaignGenerationStage.RenderLongForm => RenderLongFormAsync(jobId, cancellationToken),
            CampaignGenerationStage.GenerateSocialCopy => GenerateSocialCopyAsync(jobId, cancellationToken),
            CampaignGenerationStage.FinalizeCampaign => FinalizeAsync(jobId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    private async Task<bool> ValidatePropertyAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var valid = await dbContext.Properties.AsNoTracking().AnyAsync(candidate =>
            candidate.Id == job.PropertyId
            && candidate.OrganizationId == job.OrganizationId
            && candidate.ArchivedAtUtc == null,
            cancellationToken);
        var mediaCount = await dbContext.PropertyMedia.AsNoTracking().CountAsync(candidate =>
            candidate.PropertyId == job.PropertyId
            && candidate.OrganizationId == job.OrganizationId
            && !candidate.IsInGallery
            && candidate.WalkthroughStop != null,
            cancellationToken);
        if (!valid || mediaCount == 0)
        {
            throw new InvalidOperationException("The property is unavailable or has no listing images.");
        }

        return true;
    }

    private async Task<bool> AnalyzeMediaAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var media = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.PropertyId == job.PropertyId
                && candidate.OrganizationId == job.OrganizationId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .Select(candidate => new { candidate.AnalysisStatus, candidate.AnalysisAttemptCount })
            .ToArrayAsync(cancellationToken);
        if (media.All(candidate => candidate.AnalysisStatus == PropertyMediaAnalysisStatus.Completed))
        {
            return true;
        }

        if (media.Any(candidate => candidate.AnalysisStatus == PropertyMediaAnalysisStatus.Failed
            && candidate.AnalysisAttemptCount >= IPropertyMediaAnalysisProcessor.MaximumAttempts))
        {
            throw new InvalidOperationException("One or more listing images exhausted media-analysis retries.");
        }

        await mediaAnalysisProcessor.AnalyzeNextForPropertyAsync(
            job.OrganizationId,
            job.PropertyId,
            cancellationToken);
        return false;
    }

    private async Task<bool> GenerateStoryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        return await storyService.GenerateAsync(
            job.RequestedByUserId,
            job.PropertyId,
            cancellationToken) is not null;
    }

    private async Task<bool> GenerateMasterPlanAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var plan = await productionPlanService.GenerateAsync(
            job.RequestedByUserId,
            job.PropertyId,
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9,
            cancellationToken)
            ?? throw new InvalidOperationException("The master video plan could not be created.");
        dbContext.ChangeTracker.Clear();
        job = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        job.SetMasterPlan(plan.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> GenerateNarrationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (job.MasterVideoProductionPlanId is not { } planId)
        {
            throw new InvalidOperationException("The master plan is missing.");
        }

        var plan = await dbContext.VideoProductionPlans.AsNoTracking().SingleAsync(
            candidate => candidate.Id == planId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            SerializerOptions) ?? throw new InvalidDataException("The stored master specification is invalid.");
        var localized = ApplyAgentCardPreference(
            job,
            specification,
            await GetOrCreateLocalizationAsync(job, specification, longForm: false, cancellationToken));
        var narration = await narrationService.GenerateAsync(
            job.RequestedByUserId,
            planId,
            NarrationOptions(job, specification, localized),
            cancellationToken)
            ?? throw new InvalidOperationException("Narration could not be created.");
        dbContext.ChangeTracker.Clear();
        job = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        job.SetNarration(narration.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> GenerateDerivativePlansAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (await dbContext.CampaignDeliverables.AnyAsync(
            candidate => candidate.CampaignGenerationJobId == jobId,
            cancellationToken))
        {
            return true;
        }

        if (job.MasterVideoProductionPlanId is not { } planId)
        {
            throw new InvalidOperationException("The master plan is missing.");
        }
        if (job.VideoNarrationId is not { } shortFormNarrationId)
        {
            throw new InvalidOperationException("The campaign narration is missing.");
        }

        var plan = await dbContext.VideoProductionPlans.AsNoTracking().SingleAsync(
            candidate => candidate.Id == planId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        var yearBuilt = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == job.PropertyId && candidate.OrganizationId == job.OrganizationId)
            .Select(candidate => candidate.YearBuilt)
            .SingleAsync(cancellationToken);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            SerializerOptions)
            ?? throw new InvalidDataException("The stored master specification is invalid.");
        var localized = ApplyAgentCardPreference(
            job,
            specification,
            await GetOrCreateLocalizationAsync(job, specification, longForm: false, cancellationToken));
        specification = ApplySpokenLocalization(specification, localized);
        var captionText = localized.CaptionSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
        var shortFormTiming = await GetNarrationTimingAsync(job, shortFormNarrationId, cancellationToken);
        var media = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.PropertyId == job.PropertyId
                && candidate.OrganizationId == job.OrganizationId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .Select(candidate => new CampaignDerivativeMedia(candidate.Id, candidate.Width, candidate.Height))
            .ToArrayAsync(cancellationToken);
        var propertyVideos = await dbContext.PropertyVideos.AsNoTracking()
            .Where(candidate => candidate.PropertyId == job.PropertyId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.ProcessingStatus == PropertyVideoProcessingStatus.Completed
                && candidate.EnhancedBlobPath != null
                && candidate.EnhancedWidth != null
                && candidate.EnhancedHeight != null
                && candidate.EnhancedDurationMs != null)
            .OrderBy(candidate => candidate.UploadedAtUtc)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new CampaignDerivativeVideo(
                candidate.Id,
                candidate.EnhancedWidth!.Value,
                candidate.EnhancedHeight!.Value,
                candidate.EnhancedDurationMs!.Value))
            .ToArrayAsync(cancellationToken);
        var derivatives = derivativeGenerator.Generate(
            new CampaignDerivativeRequest(specification, media, propertyVideos));
        var acceptedScript = await UserNarrationSource.LoadAsync(
            dbContext,
            job.OrganizationId,
            job.PropertyId,
            cancellationToken);
        VideoProductionPlanResult? longFormPlan = null;
        VideoNarrationResult? longFormNarration = null;
        CampaignLocalizedCopy? longFormLocalized = null;
        var needsLongForm = job.RequestedOutputs == CampaignOutputSelection.LegacyStandard
            || job.RequestedOutputs.HasFlag(CampaignOutputSelection.FullNarratedTour);
        if (needsLongForm
            && acceptedScript is { RequiresLongForm: true })
        {
            var durationSeconds = NarrationScriptPolicy.EstimateLongFormDurationSeconds(acceptedScript.Text);
            longFormPlan = await productionPlanService.GenerateAsync(
                job.RequestedByUserId,
                job.PropertyId,
                (RequestedDuration)durationSeconds,
                VideoAspectRatio.Landscape16By9,
                cancellationToken)
                ?? throw new InvalidOperationException("The long-form video plan could not be created.");
            longFormLocalized = await GetOrCreateLocalizationAsync(
                job,
                longFormPlan.Specification,
                longForm: true,
                cancellationToken);
            longFormLocalized = ApplyAgentCardPreference(job, longFormPlan.Specification, longFormLocalized);
            longFormNarration = await narrationService.GenerateAsync(
                job.RequestedByUserId,
                longFormPlan.Id,
                NarrationOptions(job, longFormPlan.Specification, longFormLocalized),
                cancellationToken)
                ?? throw new InvalidOperationException("The long-form narration could not be created.");
            longFormPlan = longFormPlan with
            {
                Specification = ApplySpokenLocalization(longFormPlan.Specification, longFormLocalized),
            };
        }

        ShortFormDeliverable? shortForm = null;
        if (job.RequestedOutputs != CampaignOutputSelection.LegacyStandard
            && ShortFormOutputs.Any(output => job.RequestedOutputs.HasFlag(output.Selection)))
        {
            shortForm = await CreateShortFormAsync(
                job,
                specification,
                captionText,
                shortFormNarrationId,
                shortFormTiming,
                propertyVideos,
                yearBuilt,
                cancellationToken);
        }

        if (job.RequestedOutputs == CampaignOutputSelection.LegacyStandard)
        {
            foreach (var derivative in derivatives.Derivatives.Where(candidate =>
                candidate.AspectRatio == VideoAspectRatio.Landscape16By9))
            {
                AddDeliverable(job, shortFormNarrationId, ToOutputKind(derivative.Kind), derivative, yearBuilt, captionText, shortFormTiming);
            }
        }
        else
        {
            AddSelectedDeliverable(
                job,
                shortFormNarrationId,
                CampaignOutputSelection.YouTubeTour,
                CampaignOutputKind.YouTubeTour,
                derivatives.Get(CampaignDeliverableKind.Hero, VideoAspectRatio.Landscape16By9),
                yearBuilt,
                captionText,
                shortFormTiming);
            if (shortForm is not null)
            {
                foreach (var (selection, kind) in ShortFormOutputs)
                {
                    if (job.RequestedOutputs.HasFlag(selection))
                    {
                        AddDeliverable(job, shortForm.NarrationId, kind, shortForm.Specification);
                    }
                }
            }

            AddSelectedDeliverable(
                job,
                shortFormNarrationId,
                CampaignOutputSelection.FacebookFeed,
                CampaignOutputKind.FacebookFeed,
                derivatives.Get(CampaignDeliverableKind.Feature, VideoAspectRatio.Landscape16By9),
                yearBuilt,
                captionText,
                shortFormTiming);
        }

        if (longFormPlan is not null && longFormNarration is not null)
        {
            dbContext.CampaignDeliverables.Add(CampaignDeliverable.Create(
                job.Id,
                job.OrganizationId,
                job.PropertyId,
                longFormNarration.Id,
                job.RequestedOutputs == CampaignOutputSelection.LegacyStandard
                    ? CampaignOutputKind.LongForm
                    : CampaignOutputKind.FullNarratedTour,
                longFormPlan.Specification.RequestedDuration,
                longFormPlan.Specification.AspectRatio,
                JsonSerializer.Serialize(
                    CampaignCreativeDirector.Apply(
                        longFormPlan.Specification,
                        CreativeOptions(job),
                        yearBuilt,
                        longFormLocalized!.CaptionSegments.ToDictionary(
                                segment => segment.Id,
                                segment => segment.Text),
                        longFormNarration.Timing),
                    SerializerOptions),
                timeProvider.GetUtcNow()));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void AddSelectedDeliverable(
        CampaignGenerationJob job,
        Guid narrationId,
        CampaignOutputSelection selection,
        CampaignOutputKind kind,
        CampaignDerivative derivative,
        int? yearBuilt,
        IReadOnlyDictionary<string, string> captionText,
        VoiceTimingMetadata? narrationTiming)
    {
        if (job.RequestedOutputs.HasFlag(selection))
        {
            AddDeliverable(job, narrationId, kind, derivative, yearBuilt, captionText, narrationTiming);
        }
    }

    private void AddDeliverable(
        CampaignGenerationJob job,
        Guid narrationId,
        CampaignOutputKind kind,
        VideoProductionSpecification specification) =>
        dbContext.CampaignDeliverables.Add(CampaignDeliverable.Create(
            job.Id,
            job.OrganizationId,
            job.PropertyId,
            narrationId,
            kind,
            specification.RequestedDuration,
            specification.AspectRatio,
            JsonSerializer.Serialize(specification, SerializerOptions),
            timeProvider.GetUtcNow()));

    private static readonly (CampaignOutputSelection Selection, CampaignOutputKind Kind)[] ShortFormOutputs =
    [
        (CampaignOutputSelection.TikTok, CampaignOutputKind.TikTok),
        (CampaignOutputSelection.InstagramReel, CampaignOutputKind.InstagramReel),
        (CampaignOutputSelection.YouTubeShort, CampaignOutputKind.YouTubeShort),
    ];

    private sealed record ShortFormDeliverable(Guid NarrationId, VideoProductionSpecification Specification);

    /// <summary>
    /// The vertical beat-cut edit shared by TikTok, Reels and Shorts, with the voiceover the agent chose: a short
    /// voiceover written for the cut (voiced in the campaign language), none, or the full tour voiceover.
    /// </summary>
    private async Task<ShortFormDeliverable> CreateShortFormAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification master,
        IReadOnlyDictionary<string, string> masterCaptionText,
        Guid masterNarrationId,
        VoiceTimingMetadata? masterTiming,
        IReadOnlyList<CampaignDerivativeVideo> propertyVideos,
        int? yearBuilt,
        CancellationToken cancellationToken)
    {
        var options = CreativeOptions(job);
        var location = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == job.PropertyId && candidate.OrganizationId == job.OrganizationId)
            .Select(candidate => new { candidate.City, candidate.State })
            .SingleAsync(cancellationToken);
        var photos = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.PropertyId == job.PropertyId
                && candidate.OrganizationId == job.OrganizationId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .OrderBy(candidate => candidate.DisplayOrder)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new ShortFormPhoto(
                candidate.Id,
                candidate.Width,
                candidate.Height,
                candidate.SourceVideoId,
                candidate.SourceVideoTimestampMs))
            .ToArrayAsync(cancellationToken);
        IReadOnlyList<ShortFormVideoClip> videoClips = [];
        IReadOnlyList<ShortFormWalkthroughClip> walkthroughClips = [];
        if (options.Elements.HasFlag(CampaignVideoElement.WalkthroughMotion))
        {
            videoClips = await FindVideoClipsAsync(job, propertyVideos, cancellationToken);
            walkthroughClips = await FindWalkthroughClipsAsync(job, photos, cancellationToken);
        }
        else
        {
            // Without walk-through motion every shot is a still, including room photos taken from a video.
            photos = photos.Select(photo => photo with { SourceVideoId = null, SourceVideoTimestampMs = null }).ToArray();
        }

        var soundtrack = CampaignCreativeDirector.ResolveSoundtrack(options.Soundtrack, yearBuilt);
        var beatMs = 60_000 / CampaignCreativeDirector.BeatsPerMinute(
            soundtrack is CampaignSoundtrack.None or CampaignSoundtrack.AddInApp
                ? null
                : CampaignCreativeDirector.SoundtrackAssetId(soundtrack));
        VideoProductionSpecification Edit(
            ShortFormVoiceoverDetail detail,
            CampaignCreativeOptions? editOptions = null)
        {
            editOptions ??= options;
            return ShortFormEditor.Create(new ShortFormEditRequest(
                master,
                photos,
                propertyVideos,
                ShortFormCopyBuilder.DurationFor(editOptions.Voiceover),
                beatMs,
                editOptions.Pace,
                ShortFormCopyBuilder.Build(master, editOptions, location.City, location.State, detail),
                videoClips,
                walkthroughClips));
        }

        var edit = Edit(ShortFormVoiceoverDetail.HighlightRich);
        if (options.Voiceover != CampaignVoiceoverMode.ShortHook || edit.Audio.NarrationSegments.Count == 0)
        {
            // The full voiceover keeps the master's segments and timing, so its audio is reused as is; with no
            // voiceover the edit has no narration segments and the master audio is never mixed in.
            return new ShortFormDeliverable(
                masterNarrationId,
                CampaignCreativeDirector.Apply(edit, options, yearBuilt, masterCaptionText, masterTiming));
        }

        var localized = await GetOrCreateShortFormLocalizationAsync(job, edit, cancellationToken);
        VideoNarrationResult? narration = null;
        var attemptedScripts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in Enum.GetValues<ShortFormVoiceoverDetail>())
        {
            edit = Edit(detail);
            var scriptFingerprint = string.Join(
                '\n',
                edit.Audio.NarrationSegments.Select(segment => $"{segment.Id}\0{segment.Text}"));
            if (!attemptedScripts.Add(scriptFingerprint))
            {
                continue;
            }

            try
            {
                narration = await GenerateShortFormNarrationAsync(job, edit, localized, cancellationToken);
                break;
            }
            catch (InvalidDataException exception) when (NarrationRunsPastVideo(exception))
            {
                // Try a shorter grounded script. If even the CTA cannot fit, the platform cut remains useful with
                // its music, captions and end card instead of failing the entire campaign.
            }
        }

        if (narration is null)
        {
            var silentOptions = options with { Voiceover = CampaignVoiceoverMode.None };
            edit = Edit(ShortFormVoiceoverDetail.HighlightRich, silentOptions);
            return new ShortFormDeliverable(
                masterNarrationId,
                CampaignCreativeDirector.Apply(edit, silentOptions, yearBuilt, masterCaptionText, masterTiming));
        }

        var spoken = localized.SpokenSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
        edit = edit with
        {
            Audio = edit.Audio with
            {
                NarrationSegments = edit.Audio.NarrationSegments
                    .Select(segment => segment with { Text = spoken[segment.Id] })
                    .ToArray(),
            },
        };
        return new ShortFormDeliverable(
            narration.Id,
            CampaignCreativeDirector.Apply(
                edit,
                options,
                yearBuilt,
                localized.CaptionSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
                narration.Timing));
    }

    private static bool NarrationRunsPastVideo(InvalidDataException exception) =>
        exception.Message.Contains("runs past the end of the video", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest finished AI walk-through clip for each pair of consecutive storyboard photos, the same clips the
    /// tour plays between those photos.
    /// </summary>
    private async Task<IReadOnlyList<ShortFormWalkthroughClip>> FindWalkthroughClipsAsync(
        CampaignGenerationJob job,
        IReadOnlyList<ShortFormPhoto> storyboard,
        CancellationToken cancellationToken)
    {
        var pairs = storyboard.Zip(storyboard.Skip(1))
            .Select(pair => (From: pair.First.MediaId, To: pair.Second.MediaId))
            .ToHashSet();
        if (pairs.Count == 0)
        {
            return [];
        }

        var fromIds = pairs.Select(pair => pair.From).Distinct().ToArray();
        var completed = await dbContext.WalkthroughTransitions.AsNoTracking()
            .Where(transition => transition.OrganizationId == job.OrganizationId
                && transition.PropertyId == job.PropertyId
                && transition.Status == WalkthroughTransitionStatus.Completed
                && transition.AssetPath != null
                && transition.DurationMs != null
                && fromIds.Contains(transition.FromPropertyMediaId))
            .OrderByDescending(transition => transition.CompletedAtUtc)
            .ThenBy(transition => transition.Id)
            .Select(transition => new ShortFormWalkthroughClip(
                transition.Id,
                transition.FromPropertyMediaId,
                transition.ToPropertyMediaId,
                transition.DurationMs!.Value))
            .ToArrayAsync(cancellationToken);
        return completed
            .Where(clip => pairs.Contains((clip.FromMediaId, clip.ToMediaId)))
            .GroupBy(clip => (clip.FromMediaId, clip.ToMediaId))
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>
    /// The steady, detailed stretches of each finished property video, for the short-form edit to cut from. A video
    /// that cannot be read or sampled only costs its clips: the edit falls back to photos for it.
    /// </summary>
    private async Task<IReadOnlyList<ShortFormVideoClip>> FindVideoClipsAsync(
        CampaignGenerationJob job,
        IReadOnlyList<CampaignDerivativeVideo> propertyVideos,
        CancellationToken cancellationToken)
    {
        if (propertyVideos.Count == 0)
        {
            return [];
        }

        var ids = propertyVideos.Select(video => video.PropertyVideoId).ToArray();
        var paths = await dbContext.PropertyVideos.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId && ids.Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.EnhancedBlobPath })
            .ToArrayAsync(cancellationToken);
        var clips = new List<ShortFormVideoClip>();
        foreach (var video in paths.Where(video => video.EnhancedBlobPath is not null).OrderBy(video => video.Id))
        {
            try
            {
                await using var content = await propertyMediaStorage.OpenReadAsync(video.EnhancedBlobPath!, cancellationToken)
                    ?? throw new FileNotFoundException("The enhanced property video could not be opened.");
                var spans = await videoClipFinder.FindClipsAsync(content, "enhanced.mp4", cancellationToken);
                clips.AddRange(spans.Select(span => new ShortFormVideoClip(video.Id, span.StartMs, span.EndMs, span.Score)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogVideoClipsUnavailable(exception, job.Id, video.Id);
            }
        }

        return clips;
    }

    /// <summary>
    /// Saves the edit as a video plan (narration is generated per plan) and voices it. The plan is found again by
    /// its fingerprint, so a retried stage reuses both the plan and the cached narration.
    /// </summary>
    private async Task<VideoNarrationResult> GenerateShortFormNarrationAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification edit,
        CampaignLocalizedCopy localized,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(edit, SerializerOptions);
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"{ShortFormEditor.EditorVersion}\n{json}"))).ToLowerInvariant();
        var planId = await dbContext.VideoProductionPlans.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && candidate.SourceFingerprint == fingerprint)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (planId is null)
        {
            var latestVersion = await dbContext.VideoProductionPlans
                .Where(candidate => candidate.OrganizationId == job.OrganizationId
                    && candidate.PropertyId == job.PropertyId
                    && candidate.RequestedDuration == edit.RequestedDuration
                    && candidate.AspectRatio == edit.AspectRatio)
                .Select(candidate => (int?)candidate.Version)
                .MaxAsync(cancellationToken) ?? 0;
            var plan = VideoProductionPlan.Create(
                job.OrganizationId,
                job.PropertyId,
                edit.PropertyStoryId,
                latestVersion + 1,
                edit.RequestedDuration,
                edit.AspectRatio,
                edit.SchemaVersion,
                ShortFormEditor.EditorVersion,
                fingerprint,
                json);
            dbContext.VideoProductionPlans.Add(plan);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            planId = plan.Id;
        }

        var spoken = localized.SpokenSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
        return await narrationService.GenerateAsync(
            job.RequestedByUserId,
            planId.Value,
            new NarrationGenerationOptions(
                edit.Audio.NarrationSegments.Select(segment => new VoiceNarrationSegment(
                    segment.Id,
                    spoken[segment.Id],
                    segment.StartMs,
                    segment.DurationMs)).ToArray(),
                NarrationPreferences(job).SpokenLanguageCode,
                AccentInstructions(job),
                job.VoiceAccent.ToString()),
            cancellationToken)
            ?? throw new InvalidOperationException("The short-form narration could not be created.");
    }

    private async Task<CampaignLocalizedCopy> GetOrCreateShortFormLocalizationAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification edit,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(job.ShortFormLocalizedCopyJson))
        {
            var stored = JsonSerializer.Deserialize<CampaignLocalizedCopy>(
                job.ShortFormLocalizedCopyJson,
                AudioSerializerOptions)
                ?? throw new InvalidDataException("Stored short-form localization is invalid.");
            var ids = stored.SpokenSegments.Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
            if (edit.Audio.NarrationSegments.All(segment => ids.Contains(segment.Id)))
            {
                return stored;
            }
        }

        CampaignLocalizedCopy localized;
        using (aiUsagePropertyContext.BeginProperty(job.PropertyId))
        {
            localized = await localizationService.LocalizeAsync(
                new CampaignLocalizationRequest(
                    NarrationPreferences(job),
                    edit.Audio.NarrationSegments
                        .OrderBy(segment => segment.StartMs)
                        .Select(segment => new CampaignLocalizationSegment(segment.Id, segment.Text))
                        .ToArray()),
                cancellationToken);
        }
        dbContext.ChangeTracker.Clear();
        var tracked = await dbContext.CampaignGenerationJobs.SingleAsync(
            candidate => candidate.Id == job.Id,
            cancellationToken);
        tracked.SetShortFormLocalizedCopy(JsonSerializer.Serialize(localized, AudioSerializerOptions));
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return localized;
    }

    private void AddDeliverable(
        CampaignGenerationJob job,
        Guid narrationId,
        CampaignOutputKind kind,
        CampaignDerivative derivative,
        int? yearBuilt,
        IReadOnlyDictionary<string, string> captionText,
        VoiceTimingMetadata? narrationTiming) =>
        dbContext.CampaignDeliverables.Add(CampaignDeliverable.Create(
            job.Id,
            job.OrganizationId,
            job.PropertyId,
            narrationId,
            kind,
            derivative.Specification.RequestedDuration,
            derivative.AspectRatio,
            JsonSerializer.Serialize(
                CampaignCreativeDirector.Apply(
                    derivative.Specification,
                    CreativeOptions(job),
                    yearBuilt,
                    captionText,
                    narrationTiming),
                SerializerOptions),
            timeProvider.GetUtcNow()));

    private static CampaignCreativeOptions CreativeOptions(CampaignGenerationJob job) => job.CreativeOptions;

    private async Task<CampaignLocalizedCopy> GetOrCreateLocalizationAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        bool longForm,
        CancellationToken cancellationToken)
    {
        var stored = longForm ? job.LongFormLocalizedCopyJson : job.LocalizedCopyJson;
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return JsonSerializer.Deserialize<CampaignLocalizedCopy>(stored, AudioSerializerOptions)
                ?? throw new InvalidDataException("Stored campaign localization is invalid.");
        }

        CampaignLocalizedCopy localized;
        using (aiUsagePropertyContext.BeginProperty(job.PropertyId))
        {
            localized = await localizationService.LocalizeAsync(
                new CampaignLocalizationRequest(
                    NarrationPreferences(job),
                    specification.Audio.NarrationSegments
                        .OrderBy(segment => segment.StartMs)
                        .Select(segment => new CampaignLocalizationSegment(segment.Id, segment.Text))
                        .ToArray()),
                cancellationToken);
        }
        var json = JsonSerializer.Serialize(localized, AudioSerializerOptions);
        dbContext.ChangeTracker.Clear();
        var tracked = await dbContext.CampaignGenerationJobs.SingleAsync(
            candidate => candidate.Id == job.Id,
            cancellationToken);
        if (longForm)
        {
            tracked.SetLongFormLocalizedCopy(json);
        }
        else
        {
            tracked.SetLocalizedCopy(json);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return localized;
    }

    private static NarrationGenerationOptions NarrationOptions(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        CampaignLocalizedCopy localized)
    {
        var sourceById = specification.Audio.NarrationSegments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        return new NarrationGenerationOptions(
            localized.SpokenSegments.Select(segment => new VoiceNarrationSegment(
                segment.Id,
                segment.Text,
                sourceById[segment.Id].StartMs,
                sourceById[segment.Id].DurationMs)).ToArray(),
            NarrationPreferences(job).SpokenLanguageCode,
            AccentInstructions(job),
            job.VoiceAccent.ToString());
    }

    private static VideoProductionSpecification ApplySpokenLocalization(
        VideoProductionSpecification specification,
        CampaignLocalizedCopy localized)
    {
        var spoken = localized.SpokenSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
        return specification with
        {
            Audio = specification.Audio with
            {
                NarrationSegments = specification.Audio.NarrationSegments
                    .Select(segment => segment with { Text = spoken[segment.Id] })
                    .ToArray(),
            },
        };
    }

    private static CampaignLocalizedCopy ApplyAgentCardPreference(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        CampaignLocalizedCopy localized)
    {
        if (job.LocalizeAgentCard
            || !job.CreativeElements.HasFlag(CampaignVideoElement.AgentPortrait)
            || specification.Scenes.Count == 0)
        {
            return localized;
        }

        var closingSegmentIds = specification.Scenes
            .OrderBy(scene => scene.SceneNumber)
            .Last()
            .NarrationSegmentIds
            .ToHashSet(StringComparer.Ordinal);
        if (closingSegmentIds.Count == 0)
        {
            return localized;
        }

        var source = specification.Audio.NarrationSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
        CampaignLocalizedSegment Restore(CampaignLocalizedSegment segment) =>
            closingSegmentIds.Contains(segment.Id)
                ? segment with { Text = source[segment.Id] }
                : segment;
        return new CampaignLocalizedCopy(
            localized.SpokenSegments.Select(Restore).ToArray(),
            localized.CaptionSegments.Select(Restore).ToArray());
    }

    private static CampaignNarrationPreferences NarrationPreferences(CampaignGenerationJob job) => new(
        job.NarrationRegion,
        job.SpokenLanguage,
        job.CaptionLanguage,
        job.VoiceAccent,
        job.LocalizeAgentCard);

    private static string AccentInstructions(CampaignGenerationJob job) =>
        $"Use a natural {Display(job.VoiceAccent)} accent with pronunciation and delivery appropriate to {Display(job.NarrationRegion)}. Do not imitate a person or caricature a regional identity.";

    private static string Display(Enum value) => string.Concat(value.ToString().Select((character, index) =>
        index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    private async Task<bool> RenderSelectedAsync(
        Guid jobId,
        IReadOnlyList<CampaignOutputKind> kinds,
        CancellationToken cancellationToken)
    {
        var selected = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.CampaignGenerationJobId == jobId
                && kinds.Contains(deliverable.Kind))
            .OrderBy(deliverable => deliverable.Kind)
            .Select(deliverable => deliverable.Kind)
            .ToArrayAsync(cancellationToken);
        foreach (var kind in selected)
        {
            await RenderAsync(jobId, kind, cancellationToken);
        }

        return true;
    }

    private async Task<bool> RenderAsync(
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var deliverable = await dbContext.CampaignDeliverables.SingleAsync(candidate =>
            candidate.CampaignGenerationJobId == jobId && candidate.Kind == kind,
            cancellationToken);
        if (deliverable.Status == CampaignDeliverableStatus.Rendered)
        {
            return true;
        }

        var reusable = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(candidate => candidate.CampaignGenerationJobId == jobId
                && candidate.Id != deliverable.Id
                && candidate.VideoNarrationId == deliverable.VideoNarrationId
                && candidate.SpecificationJson == deliverable.SpecificationJson
                && candidate.Status == CampaignDeliverableStatus.Rendered
                && candidate.AssetPath != null
                && candidate.ContentType != null
                && candidate.FileSize != null)
            .OrderBy(candidate => candidate.RenderedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (reusable is not null)
        {
            var assetPath = $"organizations/{job.OrganizationId:N}/properties/{job.PropertyId:N}/campaigns/{job.Id:N}/{kind.ToString().ToLowerInvariant()}.mp4";
            await using var source = await campaignAssetStorage.OpenReadAsync(reusable.AssetPath!, cancellationToken)
                ?? throw new FileNotFoundException("The reusable campaign asset is unavailable.");
            var stored = false;
            try
            {
                await campaignAssetStorage.StoreAsync(assetPath, source, reusable.ContentType!, cancellationToken);
                stored = true;
                deliverable.MarkRendered(
                    assetPath,
                    reusable.ContentType!,
                    reusable.FileSize!.Value,
                    timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                if (stored)
                {
                    await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
                }

                throw;
            }

            return true;
        }

        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            deliverable.SpecificationJson,
            SerializerOptions)
            ?? throw new InvalidDataException("The stored derivative specification is invalid.");
        if (specification.Brand.Logo is not null
            || (specification.Brand.SecondaryLogo is not null
                && !specification.Brand.SecondaryLogo.StartsWith("agent-portrait:", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The campaign references unresolved brand or music assets.");
        }

        var renderPath = $"organizations/{job.OrganizationId:N}/properties/{job.PropertyId:N}/campaigns/{job.Id:N}/{kind.ToString().ToLowerInvariant()}.mp4";
        await RenderAndStoreAsync(
            job,
            specification,
            deliverable.VideoNarrationId,
            kind.ToString(),
            renderPath,
            async fileSize =>
            {
                dbContext.ChangeTracker.Clear();
                var tracked = await dbContext.CampaignDeliverables.SingleAsync(candidate =>
                    candidate.CampaignGenerationJobId == jobId && candidate.Kind == kind,
                    cancellationToken);
                tracked.MarkRendered(renderPath, "video/mp4", fileSize, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Renders a plan to a temporary file, stores it at the asset path and then runs the commit; a commit that fails
    /// removes the stored file again.
    /// </summary>
    private async Task RenderAndStoreAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        Guid narrationId,
        string label,
        string assetPath,
        Func<long, Task> commit,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-campaign-{job.Id:N}-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, $"{label.ToLowerInvariant()}.mp4");
        try
        {
            var mediaAssets = await MaterializeMediaAsync(job, specification, directory, cancellationToken);
            var narrationAsset = await MaterializeNarrationAsync(
                job,
                narrationId,
                directory,
                cancellationToken);
            var generatedClips = await MaterializeGeneratedClipsAsync(
                job,
                specification,
                directory,
                cancellationToken);
            var neighborhoodAssets = await MaterializeNeighborhoodAssetsAsync(
                job,
                specification,
                directory,
                cancellationToken);
            var propertyVideos = await MaterializePropertyVideosAsync(
                job,
                specification,
                directory,
                cancellationToken);
            var walkthroughTransitions = await MaterializeWalkthroughTransitionsAsync(
                job,
                specification,
                directory,
                cancellationToken);
            var aerialIntro = await MaterializeAerialIntroAsync(job, specification, directory, cancellationToken);
            var brandAssets = await MaterializeBrandAssetsAsync(job, specification, directory, cancellationToken);
            var agentPresenter = await MaterializeAgentPresenterAsync(
                job,
                narrationId,
                specification,
                directory,
                cancellationToken);
            var music = await campaignSoundtrackService.MaterializeAsync(
                specification.Audio.Music.AssetId,
                (int)specification.RequestedDuration * 1_000,
                directory,
                cancellationToken);
            var renderRequest = new VideoRenderRequest(
                specification,
                mediaAssets,
                narrationAsset,
                brandAssets,
                music,
                outputPath,
                generatedClips,
                neighborhoodAssets,
                propertyVideos,
                walkthroughTransitions,
                agentPresenter,
                aerialIntro);
            try
            {
                await videoRenderer.RenderAsync(renderRequest, cancellationToken);
            }
            catch (VideoRenderException exception) when (walkthroughTransitions.Length > 0 || aerialIntro is not null)
            {
                // Provider-generated walk-throughs and the drone intro are optional polish. If FFmpeg cannot decode
                // or compose one of those clips, preserve the approved tour-map photos and narration instead of
                // failing the entire campaign. The retry is deterministic and never admits media outside the
                // campaign allowlist.
                LogWalkthroughRenderFallback(
                    exception, job.Id, label, walkthroughTransitions.Length + (aerialIntro is null ? 0 : 1));
                await videoRenderer.RenderAsync(
                    renderRequest with { WalkthroughTransitions = [], AerialIntro = null },
                    cancellationToken);
            }
            var stored = false;
            try
            {
                long fileSize;
                await using (var content = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    fileSize = content.Length;
                    await campaignAssetStorage.StoreAsync(assetPath, content, "video/mp4", cancellationToken);
                }

                stored = true;
                await commit(fileSize);
            }
            catch
            {
                if (stored)
                {
                    await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
                }

                throw;
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private Task<bool> RenderLongFormAsync(Guid jobId, CancellationToken cancellationToken) =>
        RenderSelectedAsync(
            jobId,
            [CampaignOutputKind.LongForm, CampaignOutputKind.FullNarratedTour],
            cancellationToken);

    private async Task<bool> GenerateRequiredAiVideoAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (job.CreativeElements.HasFlag(CampaignVideoElement.AgentAvatar)
            && job.AgentAvatarConsentConfirmed
            && job.AgentPortraitId is { } portraitId
            && agentAvatarVideoService.IsEnabled)
        {
            var narrationIds = await dbContext.CampaignDeliverables.AsNoTracking()
                .Where(candidate => candidate.CampaignGenerationJobId == jobId)
                .Select(candidate => candidate.VideoNarrationId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            foreach (var narrationId in narrationIds)
            {
                try
                {
                    await agentAvatarVideoService.GetOrCreateAsync(
                        job.OrganizationId,
                        job.PropertyId,
                        job.RequestedByUserId,
                        portraitId,
                        narrationId,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogAgentPresenterFallback(exception, job.Id, narrationId);
                }
            }
        }

        if (job.CreativeElements.HasFlag(CampaignVideoElement.KitchenIslandOrbit))
        {
            var transitions = await dbContext.WalkthroughTransitions.AsNoTracking()
                .Where(candidate => candidate.OrganizationId == job.OrganizationId
                    && candidate.PropertyId == job.PropertyId)
                .Select(candidate => candidate.Status)
                .ToArrayAsync(cancellationToken);
            if (transitions.Any(status => status is WalkthroughTransitionStatus.Pending
                    or WalkthroughTransitionStatus.Submitting
                    or WalkthroughTransitionStatus.Generating))
            {
                return false;
            }
        }

        if (!generatedVideoClipService.IsEnabled)
        {
            return true;
        }

        var deliverables = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(candidate => candidate.CampaignGenerationJobId == jobId)
            .OrderBy(candidate => candidate.Kind)
            .ToArrayAsync(cancellationToken);
        var specifications = new Dictionary<Guid, VideoProductionSpecification>();
        foreach (var deliverable in deliverables)
        {
            var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
                deliverable.SpecificationJson,
                SerializerOptions)
                ?? throw new InvalidDataException("A stored derivative specification is invalid.");
            var scenes = new List<VideoScene>(specification.Scenes.Count);
            foreach (var scene in specification.Scenes)
            {
                if (scene.VisualSource.Kind != VisualSourceKind.GenerativeMotionRequest)
                {
                    scenes.Add(scene);
                    continue;
                }

                var mediaId = scene.VisualSource.PropertyMediaId
                    ?? throw new InvalidDataException("A generative scene is missing its source image.");
                var instruction = scene.VisualSource.GenerationInstruction
                    ?? throw new InvalidDataException("A generative scene is missing its motion instruction.");
                var clip = await generatedVideoClipService.GetOrCreateAsync(
                    job.OrganizationId,
                    job.PropertyId,
                    mediaId,
                    instruction,
                    scene.DurationMs,
                    specification.AspectRatio,
                    cancellationToken)
                    ?? throw new InvalidOperationException("AI video generation was enabled but returned no clip.");
                scenes.Add(scene with
                {
                    VisualSource = new VisualSource(
                        VisualSourceKind.GeneratedClip,
                        null,
                        clip.Id,
                        mediaId,
                        null),
                    Motion = scene.Motion with
                    {
                        Type = MotionKind.None,
                        EndViewport = scene.Motion.StartViewport,
                    },
                });
            }

            specifications[deliverable.Id] = specification with { Scenes = scenes };
        }

        dbContext.ChangeTracker.Clear();
        foreach (var deliverable in await dbContext.CampaignDeliverables
            .Where(candidate => candidate.CampaignGenerationJobId == jobId)
            .ToArrayAsync(cancellationToken))
        {
            deliverable.UpdateSpecification(JsonSerializer.Serialize(
                specifications[deliverable.Id],
                SerializerOptions));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<VideoRenderAgentPresenterAsset?> MaterializeAgentPresenterAsync(
        CampaignGenerationJob job,
        Guid narrationId,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        if (!job.CreativeElements.HasFlag(CampaignVideoElement.AgentAvatar)
            || !job.AgentAvatarConsentConfirmed
            || job.AgentPortraitId is not { } portraitId
            || specification.Brand.SecondaryLogo is null)
        {
            return null;
        }

        var presenter = await dbContext.AgentAvatarVideos.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && candidate.AgentPortraitId == portraitId
                && candidate.VideoNarrationId == narrationId)
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (presenter is null)
        {
            return null;
        }

        await using var source = await campaignAssetStorage.OpenReadAsync(presenter.AssetPath, cancellationToken);
        if (source is null)
        {
            LogAgentPresenterAssetUnavailable(job.Id, presenter.Id);
            return null;
        }

        var path = Path.Combine(directory, $"agent-presenter-{presenter.Id:N}.mp4");
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        return new VideoRenderAgentPresenterAsset(
            specification.Brand.SecondaryLogo,
            path,
            presenter.Width,
            presenter.Height,
            presenter.IntroTimelineStartMs,
            presenter.IntroDurationMs,
            presenter.OutroTimelineStartMs,
            presenter.OutroDurationMs);
    }

    private async Task<VideoRenderMediaAsset[]> MaterializeMediaAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        var ids = specification.Scenes
            .Select(scene => scene.VisualSource.Kind == VisualSourceKind.PropertyMedia
                ? scene.VisualSource.PropertyMediaId
                : scene.VisualSource.FallbackPropertyMediaId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var media = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && ids.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        if (media.Length != ids.Length)
        {
            throw new InvalidOperationException("A derivative references unavailable property media.");
        }

        var result = new List<VideoRenderMediaAsset>(media.Length);
        foreach (var item in media)
        {
            await using var source = await propertyMediaStorage.OpenReadAsync(item.BlobPath, cancellationToken)
                ?? throw new FileNotFoundException("A property-media asset could not be opened.");
            var path = Path.Combine(directory, $"media-{item.Id:N}{ExtensionFor(item.MimeType)}");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderMediaAsset(item.Id, path, item.Width, item.Height));
        }

        return [.. result];
    }

    private async Task<VoiceTimingMetadata?> GetNarrationTimingAsync(
        CampaignGenerationJob job,
        Guid narrationId,
        CancellationToken cancellationToken)
    {
        var timingJson = await dbContext.VideoNarrations.AsNoTracking()
            .Where(candidate => candidate.Id == narrationId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId)
            .Select(candidate => candidate.TimingJson)
            .SingleAsync(cancellationToken);
        return timingJson is null
            ? null
            : JsonSerializer.Deserialize<VoiceTimingMetadata>(timingJson, AudioSerializerOptions)
                ?? throw new InvalidDataException("Stored narration timing is invalid.");
    }

    private async Task<VideoRenderNarrationAsset?> MaterializeNarrationAsync(
        CampaignGenerationJob job,
        Guid narrationId,
        string directory,
        CancellationToken cancellationToken)
    {
        var narration = await dbContext.VideoNarrations.AsNoTracking().SingleAsync(candidate =>
            candidate.Id == narrationId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId,
            cancellationToken);
        await using var source = await campaignAssetStorage.OpenReadAsync(narration.AssetPath, cancellationToken)
            ?? throw new FileNotFoundException("The narration asset could not be opened.");
        var path = Path.Combine(directory, $"narration{ExtensionFor(narration.ContentType)}");
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        var timing = narration.TimingJson is null
            ? null
            : JsonSerializer.Deserialize<VoiceTimingMetadata>(narration.TimingJson, AudioSerializerOptions)
                ?? throw new InvalidDataException("Stored narration timing is invalid.");
        return new VideoRenderNarrationAsset(path, timing);
    }

    private async Task<VideoRenderBrandAsset[]> MaterializeBrandAssetsAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        if (specification.Brand.SecondaryLogo is null
            || !specification.Brand.SecondaryLogo.StartsWith("agent-portrait:", StringComparison.Ordinal)
            || job.AgentPortraitId is not { } portraitId)
        {
            return [];
        }

        var portrait = await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == portraitId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.UserId == job.RequestedByUserId,
            cancellationToken)
            ?? throw new InvalidOperationException("The campaign's agent photo is unavailable.");
        await using var source = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The campaign's agent photo could not be opened.");
        var path = Path.Combine(directory, $"agent-portrait-{portrait.Id:N}.jpg");
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        return
        [
            new VideoRenderBrandAsset(
                specification.Brand.SecondaryLogo,
                path,
                portrait.Width,
                portrait.Height),
        ];
    }

    private async Task<VideoRenderGeneratedClipAsset[]> MaterializeGeneratedClipsAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        var ids = specification.Scenes
            .Select(scene => scene.VisualSource.GeneratedClipId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var clips = await dbContext.GeneratedVideoClips.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && ids.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);

        // The short-form edit also uses finished AI walk-through clips as shots of their own.
        var walkthroughIds = ids.Except(clips.Select(clip => clip.Id)).ToArray();
        var walkthroughs = walkthroughIds.Length == 0
            ? []
            : await dbContext.WalkthroughTransitions.AsNoTracking()
                .Where(candidate => candidate.OrganizationId == job.OrganizationId
                    && candidate.PropertyId == job.PropertyId
                    && candidate.Status == WalkthroughTransitionStatus.Completed
                    && candidate.AssetPath != null
                    && candidate.DurationMs != null
                    && walkthroughIds.Contains(candidate.Id))
                .Join(
                    dbContext.PropertyMedia.AsNoTracking(),
                    transition => transition.FromPropertyMediaId,
                    media => media.Id,
                    (transition, media) => new { transition.Id, transition.AssetPath, transition.DurationMs, media.Width, media.Height })
                .ToArrayAsync(cancellationToken);
        if (clips.Length + walkthroughs.Length != ids.Length)
        {
            throw new InvalidOperationException("A derivative references an unavailable generated clip.");
        }

        var result = new List<VideoRenderGeneratedClipAsset>(ids.Length);
        foreach (var walkthrough in walkthroughs)
        {
            await using var source = await campaignAssetStorage.OpenReadAsync(walkthrough.AssetPath!, cancellationToken)
                ?? throw new FileNotFoundException("A walk-through clip could not be opened.");
            var path = Path.Combine(directory, $"walkthrough-shot-{walkthrough.Id:N}.mp4");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderGeneratedClipAsset(
                walkthrough.Id,
                path,
                walkthrough.Width,
                walkthrough.Height,
                walkthrough.DurationMs!.Value,
                LeadInMs: Math.Min(1_000, walkthrough.DurationMs!.Value / 5),
                FitToScene: true));
        }

        foreach (var clip in clips)
        {
            await using var source = await campaignAssetStorage.OpenReadAsync(clip.AssetPath, cancellationToken)
                ?? throw new FileNotFoundException("A generated video clip could not be opened.");
            var path = Path.Combine(directory, $"clip-{clip.Id:N}.mp4");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderGeneratedClipAsset(
                clip.Id,
                path,
                clip.Width,
                clip.Height,
                clip.DurationMs));
        }

        return [.. result];
    }

    /// <summary>
    /// Copies the finished walk-through clip for each pair of consecutive photo scenes. A clip that cannot be read is
    /// skipped, and that pair keeps its planned still transition.
    /// </summary>
    private async Task<VideoRenderWalkthroughTransitionAsset[]> MaterializeWalkthroughTransitionsAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        if (!job.CreativeElements.HasFlag(CampaignVideoElement.WalkthroughMotion))
        {
            return [];
        }

        var pairs = specification.Scenes.Zip(specification.Scenes.Skip(1))
            .Where(pair => pair.First.VisualSource.Kind == VisualSourceKind.PropertyMedia
                && pair.Second.VisualSource.Kind == VisualSourceKind.PropertyMedia
                && pair.First.VisualSource.PropertyMediaId is not null
                && pair.Second.VisualSource.PropertyMediaId is not null)
            .Select(pair => (From: pair.First.VisualSource.PropertyMediaId!.Value, To: pair.Second.VisualSource.PropertyMediaId!.Value))
            .Distinct()
            .ToArray();
        if (pairs.Length == 0)
        {
            return [];
        }

        var fromIds = pairs.Select(pair => pair.From).Distinct().ToArray();
        var completed = await dbContext.WalkthroughTransitions.AsNoTracking()
            .Where(transition => transition.OrganizationId == job.OrganizationId
                && transition.PropertyId == job.PropertyId
                && transition.Status == WalkthroughTransitionStatus.Completed
                && fromIds.Contains(transition.FromPropertyMediaId))
            .OrderByDescending(transition => transition.CompletedAtUtc)
            .ToArrayAsync(cancellationToken);
        var result = new List<VideoRenderWalkthroughTransitionAsset>();
        foreach (var pair in pairs)
        {
            var transition = completed.FirstOrDefault(candidate =>
                candidate.FromPropertyMediaId == pair.From && candidate.ToPropertyMediaId == pair.To);
            if (transition?.AssetPath is null || transition.DurationMs is not { } durationMs)
            {
                continue;
            }

            await using var source = await campaignAssetStorage.OpenReadAsync(transition.AssetPath, cancellationToken);
            if (source is null)
            {
                LogWalkthroughUnavailable(job.Id, transition.Id);
                continue;
            }

            var path = Path.Combine(directory, $"walkthrough-{transition.Id:N}.mp4");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderWalkthroughTransitionAsset(pair.From, pair.To, path, durationMs));
        }

        return [.. result];
    }

    /// <summary>
    /// Copies the property's drone intro when the agent approved it, it still lands on the Exterior front photo as the
    /// tour shows it today, and the video opens on that photo. Otherwise the video opens on the photo as before.
    /// </summary>
    private async Task<VideoRenderAerialIntroAsset?> MaterializeAerialIntroAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        // The drone intro is generated motion, so the photos-only choice leaves it out like walk-through clips.
        if (!job.CreativeElements.HasFlag(CampaignVideoElement.WalkthroughMotion))
        {
            return null;
        }

        var opening = specification.Scenes.Count > 0 ? specification.Scenes[0].VisualSource : null;
        if (opening?.Kind != VisualSourceKind.PropertyMedia || opening.PropertyMediaId is not { } openingPhotoId)
        {
            return null;
        }

        var clip = await dbContext.AerialIntroClips.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && candidate.PropertyMediaId == openingPhotoId
                && candidate.Status == AerialIntroClipStatus.Completed
                && candidate.ApprovedAtUtc != null,
            cancellationToken);
        if (clip?.AssetPath is null || clip.DurationMs is not { } durationMs)
        {
            return null;
        }

        var front = await AerialIntroSource.LoadFrontPhotoAsync(
            dbContext, job.OrganizationId, job.PropertyId, cancellationToken);
        if (!AerialIntroSource.IsCurrent(clip, front, walkthroughVideoProvider.AerialIntroGenerationVersion))
        {
            return null;
        }

        await using var source = await campaignAssetStorage.OpenReadAsync(clip.AssetPath, cancellationToken);
        if (source is null)
        {
            LogAerialIntroUnavailable(job.Id, clip.Id);
            return null;
        }

        var path = Path.Combine(directory, $"aerial-intro-{clip.Id:N}.mp4");
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        return new VideoRenderAerialIntroAsset(openingPhotoId, path, durationMs);
    }

    private async Task<VideoRenderPropertyVideoAsset[]> MaterializePropertyVideosAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        var ids = specification.Scenes
            .Where(scene => scene.VisualSource.Kind == VisualSourceKind.PropertyVideo)
            .Select(scene => scene.VisualSource.PropertyVideoId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var videos = await dbContext.PropertyVideos.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && candidate.ProcessingStatus == PropertyVideoProcessingStatus.Completed
                && candidate.EnhancedBlobPath != null
                && candidate.EnhancedWidth != null
                && candidate.EnhancedHeight != null
                && candidate.EnhancedDurationMs != null
                && ids.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        if (videos.Length != ids.Length)
        {
            throw new InvalidOperationException("A derivative references an unavailable enhanced property video.");
        }

        var result = new List<VideoRenderPropertyVideoAsset>(videos.Length);
        foreach (var video in videos)
        {
            await using var source = await propertyMediaStorage.OpenReadAsync(
                video.EnhancedBlobPath!, cancellationToken)
                ?? throw new FileNotFoundException("An enhanced property video could not be opened.");
            var path = Path.Combine(directory, $"property-video-{video.Id:N}.mp4");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderPropertyVideoAsset(
                video.Id,
                path,
                video.EnhancedWidth!.Value,
                video.EnhancedHeight!.Value,
                video.EnhancedDurationMs!.Value));
        }

        return [.. result];
    }

    private async Task<VideoRenderNeighborhoodAsset[]> MaterializeNeighborhoodAssetsAsync(
        CampaignGenerationJob job,
        VideoProductionSpecification specification,
        string directory,
        CancellationToken cancellationToken)
    {
        var ids = specification.FactBindings
            .Where(binding => binding.Source == FactSource.ApprovedNeighborhood)
            .Select(binding => binding.VisualAssetReferenceId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var insights = await dbContext.NeighborhoodInsights.AsNoTracking()
            .Where(insight => insight.OrganizationId == job.OrganizationId
                && insight.PropertyId == job.PropertyId
                && insight.IsApproved
                && ids.Contains(insight.Id)
                && insight.VideoPhotoBlobPath != null)
            .ToArrayAsync(cancellationToken);
        var result = new List<VideoRenderNeighborhoodAsset>(insights.Length);
        foreach (var insight in insights)
        {
            await using var source = await propertyMediaStorage.OpenReadAsync(
                insight.VideoPhotoBlobPath!, cancellationToken)
                ?? throw new FileNotFoundException("A licensed neighborhood video photo could not be opened.");
            var path = Path.Combine(
                directory,
                $"neighborhood-{insight.Id:N}{ExtensionFor(insight.VideoPhotoMimeType!)}");
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            result.Add(new VideoRenderNeighborhoodAsset(
                insight.Id,
                path,
                insight.VideoPhotoWidth!.Value,
                insight.VideoPhotoHeight!.Value,
                insight.VideoPhotoCredit!));
        }

        return [.. result];
    }

    private async Task<bool> GenerateSocialCopyAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var story = await dbContext.PropertyStories.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstAsync(cancellationToken);
        var content = story.GetContent();
        dbContext.ChangeTracker.Clear();
        job = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        job.SetSocialCopy(content.SocialCaptionLong, content.SocialCaptionShort);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (job.RequestedOutputs.HasFlag(CampaignOutputSelection.FacebookStillPost))
        {
            await RenderStillPostsAsync(jobId, content.SocialCaptionLong, cancellationToken);
        }

        return true;
    }

    private async Task RenderStillPostsAsync(
        Guid jobId,
        string caption,
        CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var formats = new[]
        {
            (CampaignStillPostKind.FacebookSquare, Width: 1080, Height: 1080),
            (CampaignStillPostKind.FacebookPortrait, Width: 1080, Height: 1350),
        };
        var rendered = await dbContext.CampaignStillPosts.AsNoTracking()
            .Where(candidate => candidate.CampaignGenerationJobId == jobId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.Status == CampaignStillPostStatus.Rendered)
            .Select(candidate => candidate.Kind)
            .ToArrayAsync(cancellationToken);
        var missing = formats.Where(format => !rendered.Contains(format.Item1)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var hero = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .OrderByDescending(candidate => candidate.HeroScore ?? 0)
            .ThenBy(candidate => candidate.DisplayOrder)
            .ThenBy(candidate => candidate.Id)
            .FirstAsync(cancellationToken);
        byte[] image;
        await using (var source = await propertyMediaStorage.OpenReadAsync(hero.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The property photo selected for the still post could not be opened."))
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            image = buffer.ToArray();
        }

        var decoded = await photoImageCodec.DecodeAsync(
            image,
            hero.MimeType,
            2_400,
            2_400,
            PhotoScaling.ShrinkToFit,
            cancellationToken);
        foreach (var format in missing)
        {
            var post = CampaignStillPost.Create(
                job.Id,
                job.OrganizationId,
                job.PropertyId,
                format.Item1,
                format.Width,
                format.Height,
                caption,
                timeProvider.GetUtcNow());
            var jpeg = await photoImageCodec.EncodeJpegAsync(
                CampaignStillPostComposer.Compose(decoded, format.Width, format.Height),
                cancellationToken);
            var assetPath = $"organizations/{job.OrganizationId:N}/properties/{job.PropertyId:N}/campaigns/{job.Id:N}/still-{format.Item1.ToString().ToLowerInvariant()}-{post.Id:N}.jpg";
            var stored = false;
            try
            {
                await using var contentStream = new MemoryStream(jpeg, writable: false);
                await campaignAssetStorage.StoreAsync(assetPath, contentStream, "image/jpeg", cancellationToken);
                stored = true;
                post.MarkRendered(assetPath, "image/jpeg", jpeg.Length, timeProvider.GetUtcNow());
                dbContext.CampaignStillPosts.Add(post);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                if (stored)
                {
                    await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
                }

                throw;
            }
        }
    }

    private async Task<bool> FinalizeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Include(candidate => candidate.Deliverables)
            .Include(candidate => candidate.StillPosts)
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        var requiredKinds = RequiredKinds(job.RequestedOutputs);
        var deliverableCountIsValid = job.RequestedOutputs == CampaignOutputSelection.LegacyStandard
            ? job.Deliverables.Count == requiredKinds.Count
                || (job.Deliverables.Count == requiredKinds.Count + 1
                    && job.Deliverables.Any(candidate => candidate.Kind == CampaignOutputKind.LongForm))
            : job.Deliverables.Count == requiredKinds.Count;
        if (!deliverableCountIsValid
            || requiredKinds.Any(kind => job.Deliverables.All(candidate => candidate.Kind != kind))
            || job.Deliverables.Any(candidate => candidate.Status != CampaignDeliverableStatus.Rendered)
            || string.IsNullOrWhiteSpace(job.SocialCaptionLong)
            || string.IsNullOrWhiteSpace(job.SocialCaptionShort)
            || (job.RequestedOutputs.HasFlag(CampaignOutputSelection.FacebookStillPost)
                && (job.StillPosts.Count != 2
                    || job.StillPosts.Any(candidate => candidate.Status != CampaignStillPostStatus.Rendered))))
        {
            throw new InvalidOperationException("The campaign is missing required deliverables or social copy.");
        }

        return true;
    }

    private static HashSet<CampaignOutputKind> RequiredKinds(CampaignOutputSelection selection)
    {
        CampaignOutputSelectionPolicy.Validate(selection);
        if (selection == CampaignOutputSelection.LegacyStandard)
        {
            return new HashSet<CampaignOutputKind>
            {
                CampaignOutputKind.Hero,
                CampaignOutputKind.Feature,
                CampaignOutputKind.Teaser,
            };
        }

        var result = new HashSet<CampaignOutputKind>();
        AddIfSelected(CampaignOutputSelection.TikTok, CampaignOutputKind.TikTok);
        AddIfSelected(CampaignOutputSelection.InstagramReel, CampaignOutputKind.InstagramReel);
        AddIfSelected(CampaignOutputSelection.YouTubeShort, CampaignOutputKind.YouTubeShort);
        AddIfSelected(CampaignOutputSelection.YouTubeTour, CampaignOutputKind.YouTubeTour);
        AddIfSelected(CampaignOutputSelection.FullNarratedTour, CampaignOutputKind.FullNarratedTour);
        AddIfSelected(CampaignOutputSelection.FacebookFeed, CampaignOutputKind.FacebookFeed);
        return result;

        void AddIfSelected(CampaignOutputSelection option, CampaignOutputKind kind)
        {
            if (selection.HasFlag(option))
            {
                result.Add(kind);
            }
        }
    }

    private async Task NotifyAsync(
        CampaignGenerationJob job,
        Func<string, JobNotificationContent> content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var property = await JobNotifications.GetPropertyLabelAsync(
            dbContext, job.OrganizationId, job.PropertyId, cancellationToken);
        await JobNotifications.AddAsync(
            dbContext,
            job.OrganizationId,
            job.PropertyId,
            job.RequestedByUserId,
            content(property),
            now,
            cancellationToken);
    }

    private Task<CampaignGenerationJob> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        dbContext.CampaignGenerationJobs.AsNoTracking().SingleAsync(
            candidate => candidate.Id == jobId,
            cancellationToken);

    private static CampaignGenerationStage Next(CampaignGenerationStage stage) => stage switch
    {
        CampaignGenerationStage.ValidateProperty => CampaignGenerationStage.AnalyzeMedia,
        CampaignGenerationStage.AnalyzeMedia => CampaignGenerationStage.GenerateStory,
        CampaignGenerationStage.GenerateStory => CampaignGenerationStage.GenerateMasterVideoPlan,
        CampaignGenerationStage.GenerateMasterVideoPlan => CampaignGenerationStage.GenerateNarration,
        CampaignGenerationStage.GenerateNarration => CampaignGenerationStage.GenerateDerivativePlans,
        CampaignGenerationStage.GenerateDerivativePlans => CampaignGenerationStage.GenerateRequiredAiVideo,
        CampaignGenerationStage.GenerateRequiredAiVideo => CampaignGenerationStage.RenderHero,
        CampaignGenerationStage.RenderHero => CampaignGenerationStage.RenderFeature,
        CampaignGenerationStage.RenderFeature => CampaignGenerationStage.RenderTeaser,
        CampaignGenerationStage.RenderTeaser => CampaignGenerationStage.RenderLongForm,
        CampaignGenerationStage.RenderLongForm => CampaignGenerationStage.GenerateSocialCopy,
        CampaignGenerationStage.GenerateSocialCopy => CampaignGenerationStage.FinalizeCampaign,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static CampaignOutputKind ToOutputKind(CampaignDeliverableKind kind) => kind switch
    {
        CampaignDeliverableKind.Hero => CampaignOutputKind.Hero,
        CampaignDeliverableKind.Feature => CampaignOutputKind.Feature,
        CampaignDeliverableKind.Teaser => CampaignOutputKind.Teaser,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        _ => throw new InvalidDataException("A campaign asset has an unsupported content type."),
    };

    private static string SanitizeError(Exception exception, bool timedOut) => timedOut
        ? "The campaign stage exceeded its configured timeout."
        : exception switch
        {
            VideoPlanValidationException => exception.Message,
            HttpRequestException => "An external provider request failed.",
            TimeoutException => "An external provider request timed out.",
            VideoRenderException => "A campaign video could not be rendered.",
            InvalidDataException => "A campaign provider returned invalid data.",
            FileNotFoundException or IOException => "A required campaign asset could not be read or written.",
            InvalidOperationException => exception.Message.Length <= 1_000
                ? exception.Message
                : "The campaign stage could not be completed.",
            _ => "The campaign stage failed unexpectedly.",
        };

    private static bool IsRetryable(Exception exception, bool timedOut) => timedOut
        || exception is HttpRequestException
            or TimeoutException
            or VideoRenderException
            or IOException;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} skipped walk-through transition {TransitionId} because its clip could not be opened")]
    private partial void LogWalkthroughUnavailable(Guid jobId, Guid transitionId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} skipped drone intro {ClipId} because its clip could not be opened")]
    private partial void LogAerialIntroUnavailable(Guid jobId, Guid clipId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} deliverable {Label} could not render {TransitionCount} walk-through or drone intro clips; retrying with approved tour-map photos")]
    private partial void LogWalkthroughRenderFallback(
        Exception exception,
        Guid jobId,
        string label,
        int transitionCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} could not find short-form clips in property video {VideoId}")]
    private partial void LogVideoClipsUnavailable(Exception exception, Guid jobId, Guid videoId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} could not generate lip-synced presenter for narration {NarrationId}; using the static agent portrait")]
    private partial void LogAgentPresenterFallback(Exception exception, Guid jobId, Guid narrationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} could not open lip-synced presenter {PresenterId}; using the static agent portrait")]
    private partial void LogAgentPresenterAssetUnavailable(Guid jobId, Guid presenterId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} stage {Stage} failed on attempt {AttemptNumber}")]
    private partial void LogStageFailure(
        Exception exception,
        Guid jobId,
        CampaignGenerationStage stage,
        int attemptNumber);
}
