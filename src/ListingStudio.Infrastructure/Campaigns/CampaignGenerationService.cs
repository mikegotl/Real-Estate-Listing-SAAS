using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Properties;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class CampaignGenerationService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage assetStorage,
    TimeProvider timeProvider,
    ICampaignBillingGate campaignBillingGate,
    IWalkthroughTransitionService walkthroughTransitionService) : ICampaignGenerationService
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

    public async Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        CancellationToken cancellationToken = default)
    {
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
        var aerialIntro = await dbContext.AerialIntroClips
            .AsNoTracking()
            .Where(clip => clip.OrganizationId == organizationId
                && clip.PropertyId == propertyId
                && clip.ApprovedAtUtc != null)
            .Select(clip => new { clip.Id, clip.PropertyMediaId, clip.SourceBlobPath, clip.ApprovedAtUtc })
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
        var existing = await dbContext.CampaignGenerationJobs
            .Include(candidate => candidate.Deliverables)
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId
                && (candidate.Status == CampaignGenerationStatus.Queued
                    || candidate.Status == CampaignGenerationStatus.Running
                    || (candidate.Status == CampaignGenerationStatus.Completed
                        && candidate.SourceFingerprint == fingerprint)))
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing);
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

        job.Retry(timeProvider.GetUtcNow());
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

        job.RequestCancellation(timeProvider.GetUtcNow());
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
        var currentIndex = (int)job.CurrentStage;
        var stageCount = Enum.GetValues<CampaignGenerationStage>().Length;
        var progress = job.Status == CampaignGenerationStatus.Completed
            ? 100
            : currentIndex * 100 / stageCount;
        var stages = Enum.GetValues<CampaignGenerationStage>()
            .Select(stage => new CampaignStageResult(
                stage,
                StageStatus(job, stage),
                stage == job.CurrentStage ? job.StageAttemptCount : 0))
            .ToArray();
        var deliverables = job.Deliverables
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
            stillPosts);
    }

    private static CampaignStageStatus StageStatus(
        CampaignGenerationJob job,
        CampaignGenerationStage stage)
    {
        if (stage < job.CurrentStage)
        {
            return CampaignStageStatus.Completed;
        }

        if (stage > job.CurrentStage)
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
