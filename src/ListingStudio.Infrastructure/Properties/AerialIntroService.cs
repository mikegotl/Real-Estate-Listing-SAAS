using System.Data;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class AerialIntroService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage campaignAssetStorage,
    IWalkthroughVideoProvider provider,
    TimeProvider timeProvider,
    ILogger<AerialIntroService> logger) : IAerialIntroService
{
    public bool IsEnabled => provider.IsEnabled;

    public async Task<AerialIntroItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var clip = await dbContext.AerialIntroClips.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId,
            cancellationToken);
        if (clip is null)
        {
            return null;
        }

        var front = await AerialIntroSource.LoadFrontPhotoAsync(dbContext, organizationId, propertyId, cancellationToken);
        return ToItem(clip, AerialIntroSource.IsCurrent(clip, front));
    }

    public async Task<AerialIntroItem> RequestAsync(
        string userId,
        Guid propertyId,
        int heightFeet,
        bool disclosureAcknowledged,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("AI drone intros are not configured.");
        }

        if (!disclosureAcknowledged)
        {
            throw new InvalidOperationException(
                "Confirm that you understand the drone intro is AI-generated before creating it.");
        }

        if (!AerialIntroClip.IsSupportedHeight(heightFeet))
        {
            throw new InvalidOperationException("Choose a starting height of 50 or 100 feet.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        string? replacedAsset;
        AerialIntroClip clip;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken))
        {
            if (!await dbContext.Properties.AnyAsync(
                    property => property.Id == propertyId
                        && property.OrganizationId == organizationId
                        && property.ArchivedAtUtc == null,
                    cancellationToken))
            {
                throw new InvalidOperationException("The property is unavailable.");
            }

            var front = await AerialIntroSource.LoadFrontPhotoAsync(dbContext, organizationId, propertyId, cancellationToken)
                ?? throw new InvalidOperationException("Add an Exterior front photo as the first stop of the tour first.");
            var existing = await dbContext.AerialIntroClips.SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId,
                cancellationToken);
            replacedAsset = existing?.AssetPath;
            if (existing is not null)
            {
                dbContext.AerialIntroClips.Remove(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            clip = AerialIntroClip.Create(
                organizationId,
                propertyId,
                front.Id,
                front.BlobPath,
                heightFeet,
                provider.AerialIntroGenerationVersion,
                userId,
                timeProvider.GetUtcNow());
            dbContext.AerialIntroClips.Add(clip);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await DeleteAssetAsync(replacedAsset);
        return ToItem(clip, isCurrent: true);
    }

    public async Task<bool> ApproveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var clip = await dbContext.AerialIntroClips.SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId,
            cancellationToken);
        if (clip is null || clip.Status != AerialIntroClipStatus.Completed)
        {
            return false;
        }

        var front = await AerialIntroSource.LoadFrontPhotoAsync(dbContext, organizationId, propertyId, cancellationToken);
        if (!AerialIntroSource.IsCurrent(clip, front))
        {
            throw new InvalidOperationException(
                "The Exterior front photo changed since this intro was made. Make a new one to match the tour.");
        }

        if (!clip.IsApproved)
        {
            clip.Approve(userId, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> RemoveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var clip = await dbContext.AerialIntroClips.SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId,
            cancellationToken);
        if (clip is null)
        {
            return false;
        }

        var assetPath = clip.AssetPath;
        dbContext.AerialIntroClips.Remove(clip);
        await dbContext.SaveChangesAsync(cancellationToken);
        await DeleteAssetAsync(assetPath);
        return true;
    }

    public async Task<AerialIntroContent?> OpenReadAsync(
        string userId,
        Guid clipId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var clip = await dbContext.AerialIntroClips.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == clipId
                && candidate.OrganizationId == organizationId
                && candidate.Status == AerialIntroClipStatus.Completed,
            cancellationToken);
        if (clip?.AssetPath is null || clip.ContentType is null)
        {
            return null;
        }

        var content = await campaignAssetStorage.OpenReadAsync(clip.AssetPath, cancellationToken);
        return content is null ? null : new AerialIntroContent(content, clip.ContentType);
    }

    // The row is already gone, so a clip file that cannot be deleted is only orphaned storage, never shown again.
    private async Task DeleteAssetAsync(string? assetPath)
    {
        if (assetPath is null)
        {
            return;
        }

        try
        {
            await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAssetDeleteFailed(exception, assetPath);
        }
    }

    private static AerialIntroItem ToItem(AerialIntroClip clip, bool isCurrent) => new(
        clip.Id,
        clip.PropertyMediaId,
        clip.HeightFeet,
        clip.Status,
        clip.Status == AerialIntroClipStatus.Failed ? clip.LastError : null,
        clip.CanRetry,
        clip.ApprovedAtUtc,
        isCurrent);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "A replaced drone intro clip at {AssetPath} could not be deleted")]
    private partial void LogAssetDeleteFailed(Exception exception, string assetPath);
}
