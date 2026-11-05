using System.Diagnostics;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// The vertical video library stages (VerticalVideoLibraryV1). Each stage run does at most one video's worth of work
/// and saves it, so a retried or resumed campaign only redoes what is missing or failed. The feature switch is read
/// again at the start of every library stage: once it is off no further video starts, finished videos are kept, and
/// a library with nothing rendered falls back to the one shared vertical video.
/// </summary>
public sealed partial class CampaignGenerationProcessor
{
    private static readonly CampaignOutputKind[] ShortFormKinds =
        [CampaignOutputKind.TikTok, CampaignOutputKind.InstagramReel, CampaignOutputKind.YouTubeShort];

    /// <summary>Where a library video is stored: campaign, library, video and version, with a non-sensitive name.</summary>
    public static string VariantAssetPath(
        Guid organizationId,
        Guid propertyId,
        Guid jobId,
        Guid libraryId,
        Guid variantId,
        VerticalCreativeFormat format,
        int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        var formatName = string.Concat(format.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
        return $"organizations/{organizationId:N}/properties/{propertyId:N}/campaigns/{jobId:N}/library/{libraryId:N}"
            + $"/variants/{variantId:N}/v{version}/{propertyId.ToString("N")[..8]}-{formatName}.mp4";
    }

    private async Task<bool> PlanVerticalLibraryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (!job.UsesVerticalLibrary
            || await dbContext.CampaignVideoLibraries.AsNoTracking().AnyAsync(
                library => library.CampaignGenerationJobId == jobId && library.OrganizationId == job.OrganizationId,
                cancellationToken))
        {
            return true;
        }

        if (!await IsLibraryEnabledAsync(job, cancellationToken))
        {
            await UseLegacyVerticalAsync(jobId, cancellationToken);
            LogVerticalLibraryFallback(jobId, "switched-off");
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        var plan = await verticalPlanningService.PlanForOrganizationAsync(
            job.OrganizationId,
            job.PropertyId,
            VerticalRecommendationContext.From(job.CreativeOptions),
            cancellationToken);
        // While the Kling native presenter is in pilot, every library video is a 15-second Kling video, so the whole
        // video is planned at that length.
        var klingPilot = job.VideoStrategy == CampaignVideoStrategy.Kling4NativePresenter
            && await IsKlingNativePresenterPilotAsync(job.OrganizationId, cancellationToken);
        var recipes = plan is null
            ? []
            : plan.Recommendation.Select(job.VerticalSelection!.Value, job.VerticalFormats)
                .Select(recommendation => plan.Recipes.FirstOrDefault(recipe => recipe.Format == recommendation.Format))
                .OfType<VerticalCreativeRecipe>()
                .Select(recipe => recipe.RetimedTo(
                    NativePresenterVideoPolicy.DurationFor(recipe.DurationSeconds, klingPilot)))
                .ToArray();
        if (plan is null || recipes.Length == 0)
        {
            // Nothing strong enough to make: the campaign keeps its one shared vertical video.
            await UseLegacyVerticalAsync(jobId, cancellationToken);
            LogVerticalLibraryFallback(jobId, "no-eligible-format");
            return true;
        }

        var now = timeProvider.GetUtcNow();
        var created = CampaignVideoLibrary.Create(
            job.OrganizationId,
            job.PropertyId,
            job.Id,
            job.VerticalSelection!.Value,
            plan.Recommendation.RuleVersion,
            plan.Recommendation.ToSnapshotJson(),
            now);
        for (var index = 0; index < recipes.Length; index++)
        {
            // Hook B (admin creative A/B tests) rewords the one chosen format's hook; without a safe hook B the video
            // keeps hook A and says so in its hook variant, so the test case sees the mismatch.
            var hookB = job.VerticalHookVariant == VerticalHookVariants.B ? VerticalHookVariants.ApplyB(recipes[index]) : null;
            created.AddVariant(hookB is null
                ? recipes[index].ToVariantPlan(index + 1)
                : hookB.ToVariantPlan(index + 1, VerticalHookVariants.B), now);
        }

        dbContext.CampaignVideoLibraries.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        VerticalVideoMetrics.RecordLibraryPlanned(recipes.Length, Stopwatch.GetElapsedTime(started));
        LogVerticalLibraryPlanned(jobId, created.Id, recipes.Length, plan.Recommendation.RuleVersion);
        return true;
    }

    private async Task<bool> GenerateVariantNarrationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var library = await LoadLibraryAsync(job, cancellationToken);
        if (library is null || CampaignVideoLibrary.IsTerminal(library.Status) || await StopLibraryIfDisabledAsync(job, cancellationToken))
        {
            return true;
        }

        var planned = await VariantsWithDeliverablesAsync(job, cancellationToken);
        var pending = library.Variants
            // A video left Generating without a plan was interrupted mid-step (worker restart, lease expiry): resume it.
            .Where(variant => variant.Status is CampaignVideoVariantStatus.Planned or CampaignVideoVariantStatus.Generating
                && !planned.Contains(variant.Id))
            .OrderBy(variant => variant.Sequence)
            .ToArray();
        if (pending.Length == 0)
        {
            return true;
        }

        var next = pending[0];
        var started = Stopwatch.GetTimestamp();
        try
        {
            await PlanVariantAsync(job, next.Id, cancellationToken);
            VerticalVideoMetrics.RecordStep("plan", next.Format, "ok", Stopwatch.GetElapsedTime(started));
        }
        catch (Exception exception) when (IsVariantFailure(exception, job))
        {
            VerticalVideoMetrics.RecordStep("plan", next.Format, "failed", Stopwatch.GetElapsedTime(started));
            await FailVariantAsync(job, next.Id, exception, cancellationToken);
        }

        return pending.Length == 1;
    }

    private async Task<bool> RenderVariantsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var library = await LoadLibraryAsync(job, cancellationToken);
        if (library is null || CampaignVideoLibrary.IsTerminal(library.Status) || await StopLibraryIfDisabledAsync(job, cancellationToken))
        {
            return true;
        }

        var variantIds = library.Variants.Select(variant => variant.Id).ToArray();
        var waiting = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.CampaignGenerationJobId == jobId
                && deliverable.OrganizationId == job.OrganizationId
                && deliverable.CampaignVideoVariantId != null
                && variantIds.Contains(deliverable.CampaignVideoVariantId.Value)
                && deliverable.Status == CampaignDeliverableStatus.Planned)
            .ToArrayAsync(cancellationToken);
        var queue = library.Variants
            .Where(variant => variant.Status is CampaignVideoVariantStatus.Planned or CampaignVideoVariantStatus.Generating
                && waiting.Any(deliverable => deliverable.CampaignVideoVariantId == variant.Id))
            .OrderBy(variant => variant.Sequence)
            .ToArray();
        if (queue.Length == 0)
        {
            return true;
        }

        // Sequential by design: one render per stage run keeps a single FFmpeg process in worker memory.
        var variant = queue[0];
        var deliverable = waiting.Single(candidate => candidate.CampaignVideoVariantId == variant.Id);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
                deliverable.SpecificationJson,
                SerializerOptions)
                ?? throw new InvalidDataException("The stored library video plan is invalid.");
            var assetPath = VariantAssetPath(
                job.OrganizationId, job.PropertyId, job.Id, library.Id, variant.Id, variant.Format, deliverable.Version);
            await RenderAndStoreAsync(
                job,
                specification,
                deliverable.VideoNarrationId,
                $"variant-{variant.Sequence}",
                assetPath,
                async fileSize =>
                {
                    dbContext.ChangeTracker.Clear();
                    var now = timeProvider.GetUtcNow();
                    var trackedDeliverable = await dbContext.CampaignDeliverables.SingleAsync(
                        candidate => candidate.Id == deliverable.Id && candidate.OrganizationId == job.OrganizationId,
                        cancellationToken);
                    var trackedVariant = await dbContext.CampaignVideoVariants.SingleAsync(
                        candidate => candidate.Id == variant.Id && candidate.OrganizationId == job.OrganizationId,
                        cancellationToken);
                    trackedDeliverable.MarkRendered(assetPath, "video/mp4", fileSize, now);
                    trackedVariant.MarkGenerating(now);
                    trackedVariant.MarkRendered(now);
                    await dbContext.SaveChangesAsync(cancellationToken);
                },
                cancellationToken,
                variantId: variant.Id);
            var elapsed = Stopwatch.GetElapsedTime(started);
            VerticalVideoMetrics.RecordStep("render", variant.Format, "ok", elapsed);
            LogVariantRendered(jobId, library.Id, variant.Id, variant.Format, (long)elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (IsVariantFailure(exception, job))
        {
            VerticalVideoMetrics.RecordStep("render", variant.Format, "failed", Stopwatch.GetElapsedTime(started));
            await FailVariantAsync(job, variant.Id, exception, cancellationToken);
        }

        return queue.Length == 1;
    }

    /// <summary>Each rendered video gets its own post caption from its own hook and call to action.</summary>
    private async Task<bool> GenerateVariantCopyAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        dbContext.ChangeTracker.Clear();
        var library = await dbContext.CampaignVideoLibraries
            .Include(candidate => candidate.Variants)
            .SingleOrDefaultAsync(
                candidate => candidate.CampaignGenerationJobId == jobId && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
        if (library is null)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var variant in library.Variants.Where(variant =>
            variant.Status == CampaignVideoVariantStatus.Rendered && variant.CaptionsJson is null))
        {
            if (VerticalCreativeRecipe.FromJson(variant.RecipeJson) is { } recipe)
            {
                variant.SetCaptions(VerticalVariantCaptions.From(recipe).ToJson(), now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return true;
    }

    private async Task<bool> FinalizeVerticalLibraryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (!job.UsesVerticalLibrary)
        {
            return true;
        }

        dbContext.ChangeTracker.Clear();
        var library = await dbContext.CampaignVideoLibraries
            .Include(candidate => candidate.Variants)
            .SingleOrDefaultAsync(
                candidate => candidate.CampaignGenerationJobId == jobId && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
        if (library is not null && !CampaignVideoLibrary.IsTerminal(library.Status))
        {
            var now = timeProvider.GetUtcNow();
            if (!await IsLibraryEnabledAsync(job, cancellationToken))
            {
                library.Cancel(now);
            }
            else
            {
                foreach (var stray in library.Variants.Where(variant => !CampaignVideoVariant.IsTerminal(variant.Status)))
                {
                    stray.MarkFailed("The video did not finish.", now);
                }

                library.Finish(now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            VerticalVideoMetrics.RecordLibraryFinished(library.Status);
            var rendered = library.Variants.Count(variant => variant.Status == CampaignVideoVariantStatus.Rendered);
            var failed = library.Variants.Count(variant => variant.Status == CampaignVideoVariantStatus.Failed);
            LogVerticalLibraryFinished(jobId, library.Id, library.Status, rendered, failed);
        }

        if (library is null || library.Variants.All(variant => variant.Status != CampaignVideoVariantStatus.Rendered))
        {
            dbContext.ChangeTracker.Clear();
            await FallBackToLegacyVerticalAsync(jobId, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Builds the video plan for one library video from its fixed recipe, voices it if the format speaks, and saves
    /// it as the video's deliverable. The narration plan is keyed by the whole plan, so the same video always reuses
    /// its own audio and two videos share audio only when everything about the voice matches.
    /// </summary>
    private async Task PlanVariantAsync(CampaignGenerationJob job, Guid variantId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow();
        var library = await dbContext.CampaignVideoLibraries
            .Include(candidate => candidate.Variants)
            .SingleAsync(
                candidate => candidate.CampaignGenerationJobId == job.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
        var variant = library.Variants.Single(candidate => candidate.Id == variantId);
        library.MarkGenerating(now);
        variant.MarkGenerating(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var recipe = VerticalCreativeRecipe.FromJson(variant.RecipeJson)
            ?? throw new InvalidOperationException("The stored recipe for this video could not be read.");
        if (job.MasterVideoProductionPlanId is not { } masterPlanId || job.VideoNarrationId is not { } masterNarrationId)
        {
            throw new InvalidOperationException("The master plan is missing.");
        }

        var masterPlan = await dbContext.VideoProductionPlans.AsNoTracking().SingleAsync(
            candidate => candidate.Id == masterPlanId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        var master = JsonSerializer.Deserialize<VideoProductionSpecification>(masterPlan.SpecificationJson, SerializerOptions)
            ?? throw new InvalidDataException("The stored master specification is invalid.");
        var photos = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId && candidate.PropertyId == job.PropertyId)
            .Select(candidate => new VerticalVariantPhotoSize(candidate.Id, candidate.Width, candidate.Height))
            .ToArrayAsync(cancellationToken);
        var videos = await dbContext.PropertyVideos.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == job.OrganizationId
                && candidate.PropertyId == job.PropertyId
                && candidate.ProcessingStatus == PropertyVideoProcessingStatus.Completed
                && candidate.EnhancedBlobPath != null
                && candidate.EnhancedWidth != null
                && candidate.EnhancedHeight != null
                && candidate.EnhancedDurationMs != null)
            .Select(candidate => new VerticalVariantVideoSize(
                candidate.Id,
                candidate.EnhancedWidth!.Value,
                candidate.EnhancedHeight!.Value,
                candidate.EnhancedDurationMs!.Value))
            .ToArrayAsync(cancellationToken);
        var yearBuilt = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == job.PropertyId && candidate.OrganizationId == job.OrganizationId)
            .Select(candidate => candidate.YearBuilt)
            .SingleAsync(cancellationToken);
        var specification = VerticalVariantSpecificationBuilder.Build(master, recipe, photos, videos);
        var options = VerticalVariantSpecificationBuilder.OptionsFor(job.CreativeOptions, recipe);
        var grounding = CaptionSceneGrounding.From(specification, await RoomsByMediaAsync(job, cancellationToken));
        var narrationId = masterNarrationId;
        VoiceTimingMetadata? timing = null;
        IReadOnlyDictionary<string, string>? captionText = null;
        if (specification.Audio.NarrationSegments.Count > 0)
        {
            var localized = await GetOrCreateVariantLocalizationAsync(job, variantId, specification, cancellationToken);
            if (NarrationPreferences(job).SpokenLanguage == CampaignLanguage.English)
            {
                // Fact cards are written for the eye ("Wildwood, FL", "2,000 sq ft"); the voice says them in words.
                localized = localized with
                {
                    SpokenSegments = localized.SpokenSegments
                        .Select(segment => segment with { Text = SpeechText.ForSpeech(segment.Text) })
                        .ToArray(),
                };
            }

            VideoNarrationResult? narration;
            try
            {
                narration = await GenerateShortFormNarrationAsync(
                    job, specification, localized, cancellationToken, VerticalVariantSpecificationBuilder.BuilderVersion);
            }
            catch (InvalidDataException exception) when (NarrationRunsPastVideo(exception)
                && job.VideoStrategy == CampaignVideoStrategy.Kling4NativePresenter
                && UsesOwnKlingVideo(variant))
            {
                // A pilot Kling video can be shorter than the edit its script was written for. Kling's own voice is
                // the soundtrack there, so the campaign voice (only a fallback) is left out rather than failing it.
                LogKlingVariantWithoutCampaignVoice(job.Id, variantId);
                narration = null;
            }
            catch (InvalidDataException exception) when (NarrationRunsPastVideo(exception))
            {
                throw new InvalidOperationException("The voiceover for this video runs longer than the video.", exception);
            }

            if (narration is null)
            {
                specification = specification with
                {
                    Audio = specification.Audio with { NarrationSegments = [] },
                };
            }
            else
            {
                var spoken = localized.SpokenSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
                specification = specification with
                {
                    Audio = specification.Audio with
                    {
                        NarrationSegments = specification.Audio.NarrationSegments
                            .Select(segment => segment with { Text = spoken[segment.Id] })
                            .ToArray(),
                    },
                };
                VerticalVideoMetrics.RecordNarrationCharacters(recipe.Format, spoken.Values.Sum(text => text.Length));
                narrationId = narration.Id;
                timing = narration.Timing;
                captionText = localized.CaptionSegments.ToDictionary(segment => segment.Id, segment => segment.Text);
                await GenerateVariantPresenterAsync(job, options, narrationId, cancellationToken);
            }
        }

        var final = Grounded(
            CampaignCreativeDirector.Apply(specification, options, yearBuilt, captionText, timing, grounding),
            grounding);
        dbContext.ChangeTracker.Clear();
        var deliverable = CampaignDeliverable.Create(
            job.Id,
            job.OrganizationId,
            job.PropertyId,
            narrationId,
            CampaignOutputKind.TikTok,
            final.RequestedDuration,
            VideoAspectRatio.Vertical9By16,
            JsonSerializer.Serialize(final, SerializerOptions),
            timeProvider.GetUtcNow());
        deliverable.AttachToVariant(variantId);
        dbContext.CampaignDeliverables.Add(deliverable);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    /// <summary>The video's voiceover in the campaign language, translated once and kept on the video.</summary>
    private async Task<CampaignLocalizedCopy> GetOrCreateVariantLocalizationAsync(
        CampaignGenerationJob job,
        Guid variantId,
        VideoProductionSpecification specification,
        CancellationToken cancellationToken)
    {
        var stored = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(candidate => candidate.Id == variantId && candidate.OrganizationId == job.OrganizationId)
            .Select(candidate => candidate.NarrationLocalizationJson)
            .SingleAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var copy = JsonSerializer.Deserialize<CampaignLocalizedCopy>(stored, AudioSerializerOptions)
                ?? throw new InvalidDataException("Stored library video localization is invalid.");
            var ids = copy.SpokenSegments.Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
            if (specification.Audio.NarrationSegments.All(segment => ids.Contains(segment.Id)))
            {
                return copy;
            }
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

        dbContext.ChangeTracker.Clear();
        var variant = await dbContext.CampaignVideoVariants.SingleAsync(
            candidate => candidate.Id == variantId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        variant.SetNarrationLocalization(JsonSerializer.Serialize(localized, AudioSerializerOptions), timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return localized;
    }

    /// <summary>
    /// The library made nothing (switched off before any video rendered, or every video failed): make the one shared
    /// vertical video instead, exactly as a legacy campaign would, so the agent still gets a vertical video.
    /// </summary>
    /// <summary>
    /// Lip-syncs the agent's consented portrait to this video's own voiceover, from the first line to the last, so the
    /// agent guides the viewer through the home. Library videos have their own voiceovers, made after the campaign's
    /// presenter step, so without this the presenter never appeared in them. A provider failure keeps the video
    /// without the presenter rather than failing it.
    /// </summary>
    private async Task GenerateVariantPresenterAsync(
        CampaignGenerationJob job,
        CampaignCreativeOptions options,
        Guid narrationId,
        CancellationToken cancellationToken)
    {
        if (!options.Elements.HasFlag(CampaignVideoElement.AgentAvatar)
            || !job.AgentAvatarConsentConfirmed
            || job.AgentPortraitId is not { } portraitId
            || !agentAvatarVideoService.IsEnabled
            || !await IncludesPremiumAiAsync(job, cancellationToken))
        {
            return;
        }

        try
        {
            await agentAvatarVideoService.GetOrCreateAsync(
                job.OrganizationId,
                job.PropertyId,
                job.RequestedByUserId,
                portraitId,
                narrationId,
                guideWholeVideo: true,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAgentPresenterFallback(exception, job.Id, narrationId);
        }
    }

    private async Task FallBackToLegacyVerticalAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        var hasLegacy = await dbContext.CampaignDeliverables.AsNoTracking().AnyAsync(
            candidate => candidate.CampaignGenerationJobId == jobId
                && (candidate.CampaignVideoVariantId == null || candidate.CampaignVideoVariantId == candidate.Id)
                && ShortFormKinds.Contains(candidate.Kind),
            cancellationToken);
        if (!hasLegacy)
        {
            if (job.MasterVideoProductionPlanId is not { } planId || job.VideoNarrationId is not { } narrationId)
            {
                throw new InvalidOperationException("The master plan is missing.");
            }

            var plan = await dbContext.VideoProductionPlans.AsNoTracking().SingleAsync(
                candidate => candidate.Id == planId && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
            var master = JsonSerializer.Deserialize<VideoProductionSpecification>(plan.SpecificationJson, SerializerOptions)
                ?? throw new InvalidDataException("The stored master specification is invalid.");
            var localized = ApplyAgentCardPreference(
                job,
                master,
                await GetOrCreateLocalizationAsync(job, master, longForm: false, cancellationToken));
            master = ApplySpokenLocalization(master, localized);
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
            var yearBuilt = await dbContext.Properties.AsNoTracking()
                .Where(candidate => candidate.Id == job.PropertyId && candidate.OrganizationId == job.OrganizationId)
                .Select(candidate => candidate.YearBuilt)
                .SingleAsync(cancellationToken);
            var shortForm = await CreateShortFormAsync(
                job,
                master,
                localized.CaptionSegments.ToDictionary(segment => segment.Id, segment => segment.Text),
                narrationId,
                await GetNarrationTimingAsync(job, narrationId, cancellationToken),
                propertyVideos,
                yearBuilt,
                cancellationToken);
            dbContext.ChangeTracker.Clear();
            var tracked = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
            foreach (var (selection, kind) in ShortFormOutputs)
            {
                if (tracked.RequestedOutputs.HasFlag(selection))
                {
                    AddDeliverable(tracked, shortForm.NarrationId, kind, shortForm.Specification);
                }
            }

            tracked.UseLegacyVertical();
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
        else
        {
            await UseLegacyVerticalAsync(jobId, cancellationToken);
        }

        LogVerticalLibraryFallback(jobId, "nothing-rendered");
        await RenderSelectedAsync(jobId, ShortFormKinds, cancellationToken);
    }

    private async Task<CampaignVideoLibrary?> LoadLibraryAsync(CampaignGenerationJob job, CancellationToken cancellationToken) =>
        job.UsesVerticalLibrary
            ? await dbContext.CampaignVideoLibraries.AsNoTracking()
                .Include(candidate => candidate.Variants)
                .SingleOrDefaultAsync(
                    candidate => candidate.CampaignGenerationJobId == job.Id && candidate.OrganizationId == job.OrganizationId,
                    cancellationToken)
            : null;

    private async Task<HashSet<Guid>> VariantsWithDeliverablesAsync(CampaignGenerationJob job, CancellationToken cancellationToken)
    {
        var ids = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(candidate => candidate.CampaignGenerationJobId == job.Id
                && candidate.OrganizationId == job.OrganizationId
                && candidate.CampaignVideoVariantId != null)
            .Select(candidate => candidate.CampaignVideoVariantId!.Value)
            .ToArrayAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private Task<bool> IsLibraryEnabledAsync(CampaignGenerationJob job, CancellationToken cancellationToken) =>
        featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, job.OrganizationId, cancellationToken);

    /// <summary>
    /// With the switch off, the library stops: unfinished videos are cancelled, rendered ones are kept, and no new
    /// video starts. Returns true when it stopped.
    /// </summary>
    private async Task<bool> StopLibraryIfDisabledAsync(CampaignGenerationJob job, CancellationToken cancellationToken)
    {
        if (await IsLibraryEnabledAsync(job, cancellationToken))
        {
            return false;
        }

        dbContext.ChangeTracker.Clear();
        var library = await dbContext.CampaignVideoLibraries
            .Include(candidate => candidate.Variants)
            .SingleOrDefaultAsync(
                candidate => candidate.CampaignGenerationJobId == job.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
        if (library is not null && !CampaignVideoLibrary.IsTerminal(library.Status))
        {
            library.Cancel(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            VerticalVideoMetrics.RecordLibraryFinished(library.Status);
            LogVerticalLibraryStopped(job.Id, library.Id);
        }

        dbContext.ChangeTracker.Clear();
        return true;
    }

    /// <summary>Cancels the library with its campaign, in the caller's unit of work. Finished videos are kept.</summary>
    private async Task CancelLibraryAsync(CampaignGenerationJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!job.UsesVerticalLibrary)
        {
            return;
        }

        var library = await dbContext.CampaignVideoLibraries
            .Include(candidate => candidate.Variants)
            .SingleOrDefaultAsync(
                candidate => candidate.CampaignGenerationJobId == job.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
        library?.Cancel(now);
    }

    private async Task UseLegacyVerticalAsync(Guid jobId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var tracked = await dbContext.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        tracked.UseLegacyVertical();
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    /// <summary>
    /// A video-level failure fails only that video. Transient provider or render errors are retried with the stage
    /// first; once the stage's attempts are spent the video is marked failed and the others carry on.
    /// </summary>
    private static bool IsVariantFailure(Exception exception, CampaignGenerationJob job) =>
        exception is not OperationCanceledException
        && (!IsRetryable(exception, timedOut: false)
            || job.StageAttemptCount >= ICampaignGenerationProcessor.MaximumStageAttempts);

    private async Task FailVariantAsync(
        CampaignGenerationJob job,
        Guid variantId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var error = SanitizeError(exception, timedOut: false);
        dbContext.ChangeTracker.Clear();
        var variant = await dbContext.CampaignVideoVariants.SingleAsync(
            candidate => candidate.Id == variantId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        if (variant.Status is CampaignVideoVariantStatus.Planned or CampaignVideoVariantStatus.Generating
            or CampaignVideoVariantStatus.Failed)
        {
            variant.MarkFailed(error, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        dbContext.ChangeTracker.Clear();
        LogVariantFailed(exception, job.Id, variantId, variant.Format);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} planned vertical library {LibraryId} with {VideoCount} videos using rules {RuleVersion}")]
    private partial void LogVerticalLibraryPlanned(Guid jobId, Guid libraryId, int videoCount, string ruleVersion);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} uses the shared vertical video instead of the library ({Reason})")]
    private partial void LogVerticalLibraryFallback(Guid jobId, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} stopped vertical library {LibraryId} because the feature was switched off")]
    private partial void LogVerticalLibraryStopped(Guid jobId, Guid libraryId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} rendered library video {VariantId} ({Format}) of library {LibraryId} in {ElapsedMs} ms")]
    private partial void LogVariantRendered(Guid jobId, Guid libraryId, Guid variantId, VerticalCreativeFormat format, long elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} library video {VariantId} ({Format}) failed")]
    private partial void LogVariantFailed(Exception exception, Guid jobId, Guid variantId, VerticalCreativeFormat format);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} finished vertical library {LibraryId} as {Status}: {Rendered} rendered, {Failed} failed")]
    private partial void LogVerticalLibraryFinished(
        Guid jobId, Guid libraryId, CampaignVideoLibraryStatus status, int rendered, int failed);
}
