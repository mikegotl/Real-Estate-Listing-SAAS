using System.Data;
using System.Data.Common;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class PropertyPhotoFixService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPhotoClutterRemover clutterRemover,
    IWalkthroughTransitionService walkthroughTransitions,
    TimeProvider timeProvider,
    ILogger<PropertyPhotoFixService> logger) : IPropertyPhotoFixService
{
    public bool IsClutterRemovalEnabled => clutterRemover.IsEnabled;

    public async Task<IReadOnlyList<PropertyPhotoFixItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var fixes = await dbContext.PropertyPhotoFixes.AsNoTracking()
            .Where(fix => fix.OrganizationId == organizationId
                && fix.PropertyId == propertyId
                && fix.Status != PropertyPhotoFixStatus.Completed)
            .ToListAsync(cancellationToken);
        if (fixes.Count == 0)
        {
            return [];
        }

        var editedAt = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .ToDictionaryAsync(media => media.Id, media => media.EditedAtUtc, cancellationToken);
        return fixes
            .Where(fix => editedAt.ContainsKey(fix.PropertyMediaId))
            .GroupBy(fix => fix.PropertyMediaId)
            .Select(group => group.OrderByDescending(fix => fix.RequestedAtUtc).First())
            // A failure from before the photo last changed no longer describes the photo shown.
            .Where(fix => fix.IsInProgress || editedAt[fix.PropertyMediaId] is not { } changed || fix.RequestedAtUtc >= changed)
            .Select(fix => new PropertyPhotoFixItem(
                fix.PropertyMediaId,
                fix.Id,
                fix.Kind,
                fix.Status,
                fix.Status == PropertyPhotoFixStatus.Failed ? fix.LastError : null,
                fix.IsInProgress))
            .ToArray();
    }

    public async Task<bool> RequestAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        PhotoFix fix,
        CancellationToken cancellationToken = default)
    {
        if (fix is not (PhotoFix.BrightnessAndColor or PhotoFix.RemoveClutter or PhotoFix.LevelAndCrop))
        {
            throw new ArgumentOutOfRangeException(nameof(fix), "Choose one photo fix.");
        }

        if (fix == PhotoFix.RemoveClutter && !clutterRemover.IsEnabled)
        {
            throw new InvalidOperationException("Remove clutter is not available yet.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var media = await FindEditableMediaAsync(organizationId, propertyId, mediaId, cancellationToken);
        if (media is null)
        {
            return false;
        }

        await EnsureNoFixRunningAsync(organizationId, mediaId, cancellationToken);
        dbContext.PropertyPhotoFixes.Add(PropertyPhotoFix.Create(
            organizationId,
            propertyId,
            mediaId,
            fix,
            media.BlobPath,
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevertAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        string fixedBlobPath;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken))
        {
            var media = await FindEditableMediaAsync(organizationId, propertyId, mediaId, cancellationToken);
            if (media is null || !media.IsEdited)
            {
                return false;
            }

            await EnsureNoFixRunningAsync(organizationId, mediaId, cancellationToken);
            fixedBlobPath = media.RevertToOriginal(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await storage.DeleteAsync(fixedBlobPath, cancellationToken);
        await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        return true;
    }

    public async Task<PropertyMediaContent?> OpenOriginalReadAsync(
        string userId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var media = await dbContext.PropertyMedia.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == mediaId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (media?.OriginalBlobPath is null || media.OriginalMimeType is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(media.OriginalBlobPath, cancellationToken);
        return content is null ? null : new PropertyMediaContent(content, media.OriginalMimeType, media.OriginalFilename);
    }

    private async Task<PropertyMedia?> FindEditableMediaAsync(
        Guid organizationId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Properties.AnyAsync(
                property => property.Id == propertyId
                    && property.OrganizationId == organizationId
                    && property.ArchivedAtUtc == null,
                cancellationToken))
        {
            return null;
        }

        return await dbContext.PropertyMedia.SingleOrDefaultAsync(
            media => media.Id == mediaId && media.PropertyId == propertyId && media.OrganizationId == organizationId,
            cancellationToken);
    }

    private async Task EnsureNoFixRunningAsync(Guid organizationId, Guid mediaId, CancellationToken cancellationToken)
    {
        var unfinished = await dbContext.PropertyPhotoFixes
            .Where(fix => fix.OrganizationId == organizationId
                && fix.PropertyMediaId == mediaId
                && fix.Status != PropertyPhotoFixStatus.Completed)
            .ToListAsync(cancellationToken);
        if (unfinished.Any(fix => fix.IsInProgress))
        {
            throw new InvalidOperationException("This photo is still being fixed. Try again when it finishes.");
        }
    }

    // The photo is already saved; a clip refresh that loses a race with another edit is logged rather than undoing
    // it, like the storyboard edits do.
    private async Task RequeueAffectedClipsAsync(string userId, Guid propertyId, CancellationToken cancellationToken)
    {
        try
        {
            await walkthroughTransitions.RequeueAffectedAsync(userId, propertyId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            LogClipRefreshFailed(exception, propertyId);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through clips for property {PropertyId} were not requeued after a photo was reverted")]
    private partial void LogClipRefreshFailed(Exception exception, Guid propertyId);
}
