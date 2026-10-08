using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class CampaignGenerationService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage assetStorage,
    TimeProvider timeProvider,
    IBillingUsageRecorder billingUsageRecorder) : ICampaignGenerationService
{
    private const string WorkflowVersion = "campaign-accepted-script-walkthrough-v6";

    public async Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
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
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .OrderBy(candidate => candidate.DisplayOrder)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.DisplayOrder,
                candidate.UploadedAt,
            })
            .ToArrayAsync(cancellationToken);
        if (media.Length == 0)
        {
            throw new InvalidOperationException("At least one property image is required to generate a campaign.");
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
        var acceptedScript = await dbContext.PropertyNarrationScripts
            .AsNoTracking()
            .Where(script => script.OrganizationId == organizationId
                && script.PropertyId == propertyId
                && script.MarketingUseAccepted)
            .Select(script => new
            {
                script.Id,
                script.ExtractedText,
                script.MarketingUseAcceptedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken);
        var walkthroughVideos = await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId
                && video.PropertyId == propertyId
                && video.ProcessingStatus == Domain.Properties.PropertyVideoProcessingStatus.Completed)
            .OrderBy(video => video.Id)
            .Select(video => new
            {
                video.Id,
                video.EnhancedBlobPath,
                video.EnhancedDurationMs,
                video.EnhancementVersion,
                video.ProcessingCompletedAtUtc,
            })
            .ToArrayAsync(cancellationToken);
        var fingerprint = CreateFingerprint(
            property.UpdatedAtUtc,
            media,
            neighborhoodFacts,
            acceptedScript,
            walkthroughVideos);
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

        var job = CampaignGenerationJob.Create(
            organizationId,
            propertyId,
            userId,
            fingerprint,
            timeProvider.GetUtcNow());
        dbContext.CampaignGenerationJobs.Add(job);
        await billingUsageRecorder.RecordCampaignAsync(organizationId, job.Id, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    private IQueryable<CampaignGenerationJob> Query(Guid organizationId) => dbContext.CampaignGenerationJobs
        .AsNoTracking()
        .Include(candidate => candidate.Deliverables)
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
        object walkthroughVideos)
    {
        var source = JsonSerializer.Serialize(new
        {
            workflowVersion = WorkflowVersion,
            propertyUpdatedAtUtc,
            media,
            neighborhoodFacts,
            acceptedScript,
            walkthroughVideos,
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
                item.RenderedAtUtc))
            .ToArray();
        return new CampaignGenerationResult(
            job.Id,
            job.PropertyId,
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
            deliverables);
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
