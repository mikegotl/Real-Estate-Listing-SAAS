using System.Data;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
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
    ICampaignDerivativeGenerator derivativeGenerator,
    IGeneratedVideoClipService generatedVideoClipService,
    IVideoRenderer videoRenderer,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
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

        var (jobId, stage, attemptNumber) = claimed.Value;
        using var stageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stageTimeout.CancelAfter(TimeSpan.FromSeconds(options.Value.StageTimeoutSeconds));
        try
        {
            var completed = await ExecuteStageAsync(jobId, stage, stageTimeout.Token);
            dbContext.ChangeTracker.Clear();
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
            }
            else
            {
                job.CompleteStage(Next(stage), now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
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
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
            return new CampaignStageRunResult(jobId, stage, false, false, willRetry, attemptNumber, error);
        }
    }

    private async Task<(Guid JobId, CampaignGenerationStage Stage, int AttemptNumber)?> ClaimNextAsync(
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

        job.BeginStage(now, TimeSpan.FromSeconds(options.Value.LeaseSeconds));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = (job.Id, job.CurrentStage, job.StageAttemptCount);
        dbContext.ChangeTracker.Clear();
        return result;
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
            CampaignGenerationStage.RenderHero => RenderAsync(jobId, CampaignOutputKind.Hero, cancellationToken),
            CampaignGenerationStage.RenderFeature => RenderAsync(jobId, CampaignOutputKind.Feature, cancellationToken),
            CampaignGenerationStage.RenderTeaser => RenderAsync(jobId, CampaignOutputKind.Teaser, cancellationToken),
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
            candidate.PropertyId == job.PropertyId && candidate.OrganizationId == job.OrganizationId,
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
                && candidate.OrganizationId == job.OrganizationId)
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

        var narration = await narrationService.GenerateAsync(job.RequestedByUserId, planId, cancellationToken)
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

        var plan = await dbContext.VideoProductionPlans.AsNoTracking().SingleAsync(
            candidate => candidate.Id == planId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            SerializerOptions)
            ?? throw new InvalidDataException("The stored master specification is invalid.");
        var media = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.PropertyId == job.PropertyId
                && candidate.OrganizationId == job.OrganizationId)
            .Select(candidate => new CampaignDerivativeMedia(candidate.Id, candidate.Width, candidate.Height))
            .ToArrayAsync(cancellationToken);
        var derivatives = derivativeGenerator.Generate(new CampaignDerivativeRequest(specification, media));
        foreach (var derivative in derivatives.Derivatives.Where(candidate =>
            candidate.AspectRatio == VideoAspectRatio.Landscape16By9))
        {
            dbContext.CampaignDeliverables.Add(CampaignDeliverable.Create(
                job.Id,
                job.OrganizationId,
                job.PropertyId,
                ToOutputKind(derivative.Kind),
                derivative.Specification.RequestedDuration,
                derivative.AspectRatio,
                JsonSerializer.Serialize(derivative.Specification, SerializerOptions),
                timeProvider.GetUtcNow()));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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

        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            deliverable.SpecificationJson,
            SerializerOptions)
            ?? throw new InvalidDataException("The stored derivative specification is invalid.");
        if (specification.Brand.Logo is not null
            || specification.Brand.SecondaryLogo is not null
            || specification.Audio.Music.AssetId is not null)
        {
            throw new InvalidOperationException("The campaign references unresolved brand or music assets.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-campaign-{jobId:N}-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, $"{kind.ToString().ToLowerInvariant()}.mp4");
        try
        {
            var mediaAssets = await MaterializeMediaAsync(job, specification, directory, cancellationToken);
            var narrationAsset = await MaterializeNarrationAsync(job, directory, cancellationToken);
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
            await videoRenderer.RenderAsync(
                new VideoRenderRequest(
                    specification,
                    mediaAssets,
                    narrationAsset,
                    [],
                    null,
                    outputPath,
                    generatedClips,
                    neighborhoodAssets),
                cancellationToken);
            var assetPath = $"organizations/{job.OrganizationId:N}/properties/{job.PropertyId:N}/campaigns/{job.Id:N}/{kind.ToString().ToLowerInvariant()}.mp4";
            var stored = false;
            try
            {
                await using var content = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var fileSize = content.Length;
                await campaignAssetStorage.StoreAsync(assetPath, content, "video/mp4", cancellationToken);
                stored = true;
                dbContext.ChangeTracker.Clear();
                deliverable = await dbContext.CampaignDeliverables.SingleAsync(candidate =>
                    candidate.CampaignGenerationJobId == jobId && candidate.Kind == kind,
                    cancellationToken);
                deliverable.MarkRendered(assetPath, "video/mp4", fileSize, timeProvider.GetUtcNow());
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
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return true;
    }

    private async Task<bool> GenerateRequiredAiVideoAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (!generatedVideoClipService.IsEnabled)
        {
            return true;
        }

        var job = await GetJobAsync(jobId, cancellationToken);
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

    private async Task<VideoRenderNarrationAsset?> MaterializeNarrationAsync(
        CampaignGenerationJob job,
        string directory,
        CancellationToken cancellationToken)
    {
        if (job.VideoNarrationId is not { } narrationId)
        {
            return null;
        }

        var narration = await dbContext.VideoNarrations.AsNoTracking().SingleAsync(candidate =>
            candidate.Id == narrationId && candidate.OrganizationId == job.OrganizationId,
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
        if (clips.Length != ids.Length)
        {
            throw new InvalidOperationException("A derivative references an unavailable generated clip.");
        }

        var result = new List<VideoRenderGeneratedClipAsset>(clips.Length);
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
        return true;
    }

    private async Task<bool> FinalizeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Include(candidate => candidate.Deliverables)
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job.Deliverables.Count != 3
            || job.Deliverables.Any(candidate => candidate.Status != CampaignDeliverableStatus.Rendered)
            || string.IsNullOrWhiteSpace(job.SocialCaptionLong)
            || string.IsNullOrWhiteSpace(job.SocialCaptionShort))
        {
            throw new InvalidOperationException("The campaign is missing required deliverables or social copy.");
        }

        return true;
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
        CampaignGenerationStage.RenderTeaser => CampaignGenerationStage.GenerateSocialCopy,
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
        Message = "Campaign {JobId} stage {Stage} failed on attempt {AttemptNumber}")]
    private partial void LogStageFailure(
        Exception exception,
        Guid jobId,
        CampaignGenerationStage stage,
        int attemptNumber);
}
