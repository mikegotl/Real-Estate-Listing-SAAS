using System.Data;
using System.Data.Common;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class PropertyMediaService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    ICampaignAssetStorage campaignAssetStorage,
    IWalkthroughTransitionService walkthroughTransitions,
    TimeProvider timeProvider,
    ILogger<PropertyMediaService> logger) : IPropertyMediaService
{
    private static readonly Dictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = "JPEG",
            ["image/jpg"] = "JPEG",
            ["image/png"] = "PNG",
            ["image/webp"] = "WEBP",
        };

    private static readonly Dictionary<string, string> AllowedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "JPEG",
            [".jpeg"] = "JPEG",
            [".png"] = "PNG",
            [".webp"] = "WEBP",
        };

    public async Task<IReadOnlyList<PropertyMediaItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: true, cancellationToken))
        {
            return [];
        }

        return await dbContext.PropertyMedia
            .AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .OrderBy(media => media.DisplayOrder)
            .Select(media => ToItem(media))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyMediaUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);
        var walkthroughStop = WalkthroughStoryboard.Normalize(upload.WalkthroughStop);
        if (upload.ToGallery && walkthroughStop is not null)
        {
            throw new ArgumentException("A photo goes either to a walkthrough stop or to the gallery.", nameof(upload));
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            throw new InvalidOperationException("The property is unavailable for media uploads.");
        }

        var existingCount = await dbContext.PropertyMedia.CountAsync(
            media => media.OrganizationId == organizationId && media.PropertyId == propertyId,
            cancellationToken);
        if (existingCount >= IPropertyMediaService.MaximumMediaPerProperty)
        {
            throw new InvalidOperationException(
                $"A property can have at most {IPropertyMediaService.MaximumMediaPerProperty} images.");
        }

        var validated = await ValidateAndBufferAsync(upload, cancellationToken);
        await using var bufferedContent = validated.Content;
        var blobPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/{Guid.NewGuid():N}{validated.Extension}";
        var media = PropertyMedia.Create(
            organizationId,
            propertyId,
            blobPath,
            validated.Filename,
            validated.ContentType,
            bufferedContent.Length,
            validated.Width,
            validated.Height,
            existingCount);
        media.SetWalkthroughStop(walkthroughStop);
        if (upload.ToGallery)
        {
            media.MoveToGallery();
        }

        await storage.StoreAsync(blobPath, bufferedContent, validated.ContentType, cancellationToken);
        try
        {
            dbContext.PropertyMedia.Add(media);
            if (walkthroughStop is not null)
            {
                var existing = await dbContext.PropertyMedia
                    .Where(candidate => candidate.OrganizationId == organizationId
                        && candidate.PropertyId == propertyId
                        && candidate.Id != media.Id)
                    .ToListAsync(cancellationToken);
                ApplyStoryboardOrder([.. existing, media]);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception persistenceError)
        {
            try
            {
                await storage.DeleteAsync(blobPath, cancellationToken);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Property media persistence failed and the stored object could not be cleaned up.",
                    persistenceError,
                    cleanupError);
            }

            throw;
        }

        if (!upload.ToGallery)
        {
            await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        }

        return media.Id;
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var media = await dbContext.PropertyMedia.SingleOrDefaultAsync(
            candidate => candidate.Id == mediaId
                && candidate.PropertyId == propertyId
                && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (media is null)
        {
            return false;
        }

        var generatedClips = await dbContext.GeneratedVideoClips
            .Where(clip => clip.PropertyMediaId == mediaId
                && clip.PropertyId == propertyId
                && clip.OrganizationId == organizationId)
            .ToArrayAsync(cancellationToken);
        foreach (var clip in generatedClips)
        {
            await campaignAssetStorage.DeleteAsync(clip.AssetPath, cancellationToken);
        }

        var transitions = await dbContext.WalkthroughTransitions
            .Where(transition => (transition.FromPropertyMediaId == mediaId || transition.ToPropertyMediaId == mediaId)
                && transition.PropertyId == propertyId
                && transition.OrganizationId == organizationId)
            .ToArrayAsync(cancellationToken);
        foreach (var assetPath in transitions.Select(transition => transition.AssetPath).OfType<string>())
        {
            await campaignAssetStorage.DeleteAsync(assetPath, cancellationToken);
        }

        await storage.DeleteAsync(media.BlobPath, cancellationToken);
        if (media.OriginalBlobPath is not null)
        {
            await storage.DeleteAsync(media.OriginalBlobPath, cancellationToken);
        }

        dbContext.GeneratedVideoClips.RemoveRange(generatedClips);
        dbContext.WalkthroughTransitions.RemoveRange(transitions);
        dbContext.PropertyMedia.Remove(media);
        await dbContext.SaveChangesAsync(cancellationToken);
        await NormalizeDisplayOrderAsync(organizationId, propertyId, cancellationToken);
        await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        return true;
    }

    public async Task<bool> ReorderAsync(
        string userId,
        Guid propertyId,
        IReadOnlyList<Guid> orderedMediaIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMediaIds);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var media = await dbContext.PropertyMedia
            .Where(candidate => candidate.PropertyId == propertyId && candidate.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var storyboard = media.Where(candidate => !candidate.IsInGallery).ToList();
        if (orderedMediaIds.Count != storyboard.Count
            || orderedMediaIds.Distinct().Count() != storyboard.Count
            || storyboard.Any(candidate => !orderedMediaIds.Contains(candidate.Id)))
        {
            throw new ArgumentException(
                "The order must include each storyboard image exactly once.",
                nameof(orderedMediaIds));
        }

        var byId = storyboard.ToDictionary(candidate => candidate.Id);
        for (var index = 0; index < orderedMediaIds.Count; index++)
        {
            byId[orderedMediaIds[index]].SetDisplayOrder(index);
        }

        ApplyStoryboardOrder(media);
        await dbContext.SaveChangesAsync(cancellationToken);
        await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        return true;
    }

    public async Task<bool> SetWalkthroughStopAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        string? walkthroughStop,
        CancellationToken cancellationToken = default)
    {
        var normalizedStop = WalkthroughStoryboard.Normalize(walkthroughStop);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var media = await dbContext.PropertyMedia
            .Where(candidate => candidate.PropertyId == propertyId && candidate.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var target = media.SingleOrDefault(candidate => candidate.Id == mediaId);
        if (target is null)
        {
            return false;
        }

        if (!string.Equals(target.WalkthroughStop, normalizedStop, StringComparison.Ordinal))
        {
            var others = media.Where(item => item.Id != mediaId && !item.IsInGallery).ToArray();
            var newStopOrder = normalizedStop is null
                || others.Any(item => string.Equals(item.WalkthroughStop, normalizedStop, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : WalkthroughStoryboard.NewStopOrder(others, item => item.WalkthroughStop, item => item.DisplayOrder, normalizedStop);
            target.SetWalkthroughStop(normalizedStop);
            if (newStopOrder is { } order)
            {
                // A new indoor stop opens just before the outdoor stops that end the walkthrough.
                foreach (var item in media.Where(item => item.DisplayOrder >= order))
                {
                    item.SetDisplayOrder(item.DisplayOrder + 1);
                }

                target.SetDisplayOrder(order);
            }
            else
            {
                // Moving past every other photo places it last within its new stop once the storyboard is applied.
                target.SetDisplayOrder(media.Count);
            }

            ApplyStoryboardOrder(media);
            await dbContext.SaveChangesAsync(cancellationToken);
            await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        }

        return true;
    }

    public async Task<bool> MoveToGalleryAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var media = await dbContext.PropertyMedia
            .Where(candidate => candidate.PropertyId == propertyId && candidate.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var target = media.SingleOrDefault(candidate => candidate.Id == mediaId);
        if (target is null)
        {
            return false;
        }

        if (!target.IsInGallery)
        {
            target.MoveToGallery();
            // The newest gallery photo goes last in the gallery.
            target.SetDisplayOrder(media.Count);
            ApplyStoryboardOrder(media);
            await dbContext.SaveChangesAsync(cancellationToken);
            await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<WalkthroughStopDirectionItem>> ListStopDirectionsAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.WalkthroughStopDirections
            .AsNoTracking()
            .Where(direction => direction.OrganizationId == organizationId && direction.PropertyId == propertyId)
            .OrderBy(direction => direction.Stop)
            .Select(direction => new WalkthroughStopDirectionItem(direction.Stop, direction.Direction))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> SetStopDirectionAsync(
        string userId,
        Guid propertyId,
        string walkthroughStop,
        WalkthroughDirection? direction,
        CancellationToken cancellationToken = default)
    {
        var stop = WalkthroughStoryboard.Normalize(walkthroughStop)
            ?? throw new ArgumentException("A walkthrough stop is required.", nameof(walkthroughStop));
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        // Stops match case-insensitively, like the storyboard groups them, so "sunroom" and "Sunroom" share a note.
        var existing = (await dbContext.WalkthroughStopDirections
                .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
                .ToListAsync(cancellationToken))
            .Where(candidate => string.Equals(candidate.Stop, stop, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (direction is null)
        {
            dbContext.WalkthroughStopDirections.RemoveRange(existing);
        }
        else if (existing.Count == 0)
        {
            dbContext.WalkthroughStopDirections.Add(WalkthroughStopDirection.Create(
                organizationId,
                propertyId,
                stop,
                direction.Value,
                timeProvider.GetUtcNow()));
        }
        else
        {
            existing[0].SetDirection(direction.Value, timeProvider.GetUtcNow());
            dbContext.WalkthroughStopDirections.RemoveRange(existing.Skip(1));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await RequeueAffectedClipsAsync(userId, propertyId, cancellationToken);
        return true;
    }

    public async Task<PropertyMediaContent?> OpenReadAsync(
        string userId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var media = await dbContext.PropertyMedia
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == mediaId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (media is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(media.BlobPath, cancellationToken);
        return content is null ? null : new PropertyMediaContent(content, media.MimeType, media.OriginalFilename);
    }

    public async Task<bool> RetryAnalysisAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var media = await dbContext.PropertyMedia.SingleOrDefaultAsync(
            candidate => candidate.Id == mediaId
                && candidate.PropertyId == propertyId
                && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (media is null || media.AnalysisStatus != PropertyMediaAnalysisStatus.Failed)
        {
            return false;
        }

        media.QueueAnalysisRetry();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The edit is already saved; a clip refresh that loses a race with another edit is logged rather than undoing
    // it, and the next edit or the Create walk-through clips button queues what is still missing.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through clips for property {PropertyId} were not requeued after a storyboard edit")]
    private partial void LogClipRefreshFailed(Exception exception, Guid propertyId);

    // The storyboard comes first in display order; gallery photos follow it, outside the walkthrough.
    private static void ApplyStoryboardOrder(IReadOnlyList<PropertyMedia> media)
    {
        var arranged = WalkthroughStoryboard
            .Arrange(media.Where(item => !item.IsInGallery), item => item.WalkthroughStop, item => item.DisplayOrder)
            .Concat(media.Where(item => item.IsInGallery).OrderBy(item => item.DisplayOrder))
            .ToArray();
        for (var index = 0; index < arranged.Length; index++)
        {
            arranged[index].SetDisplayOrder(index);
        }
    }

    private async Task NormalizeDisplayOrderAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var remaining = await dbContext.PropertyMedia
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .OrderBy(media => media.DisplayOrder)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < remaining.Count; index++)
        {
            remaining[index].SetDisplayOrder(index);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<bool> PropertyExistsAsync(
        Guid organizationId,
        Guid propertyId,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        return dbContext.Properties.AnyAsync(
            property => property.Id == propertyId
                && property.OrganizationId == organizationId
                && (includeArchived || property.ArchivedAtUtc == null),
            cancellationToken);
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

    private static async Task<ValidatedUpload> ValidateAndBufferAsync(
        PropertyMediaUpload upload,
        CancellationToken cancellationToken)
    {
        var filename = Path.GetFileName(upload.OriginalFilename).Trim();
        if (filename.Length is 0 or > 255)
        {
            throw new InvalidDataException("The original filename is required and cannot exceed 255 characters.");
        }

        if (upload.FileSize <= 0 || upload.FileSize > IPropertyMediaService.MaximumFileSize)
        {
            throw new InvalidDataException(
                $"Images must be between 1 byte and {IPropertyMediaService.MaximumFileSize / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var expectedFormat))
        {
            throw new InvalidDataException("Only JPG, JPEG, PNG, and WEBP images are allowed.");
        }

        if (!AllowedContentTypes.TryGetValue(upload.ContentType, out var contentTypeFormat)
            || !string.Equals(expectedFormat, contentTypeFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The image MIME type does not match its filename extension.");
        }

        var buffer = new MemoryStream((int)Math.Min(upload.FileSize, 64 * 1024));
        var copyBuffer = new byte[64 * 1024];
        while (buffer.Length <= upload.FileSize)
        {
            var remaining = upload.FileSize - buffer.Length + 1;
            var bytesRead = await upload.Content.ReadAsync(
                copyBuffer.AsMemory(0, (int)Math.Min(copyBuffer.Length, remaining)),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await buffer.WriteAsync(copyBuffer.AsMemory(0, bytesRead), cancellationToken);
        }

        if (buffer.Length != upload.FileSize || buffer.Length > IPropertyMediaService.MaximumFileSize)
        {
            await buffer.DisposeAsync();
            throw new InvalidDataException("The uploaded image size did not match the declared file size.");
        }

        try
        {
            var image = ImageMetadataReader.Read(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            if (!string.Equals(image.Format, expectedFormat, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The file content is not a supported image format.");
            }

            if (image.Width > IPropertyMediaService.MaximumImageDimension
                || image.Height > IPropertyMediaService.MaximumImageDimension
                || (long)image.Width * image.Height > IPropertyMediaService.MaximumPixelCount)
            {
                throw new InvalidDataException(
                    $"Images cannot exceed {IPropertyMediaService.MaximumImageDimension} pixels on either side "
                    + $"or {IPropertyMediaService.MaximumPixelCount:N0} total pixels.");
            }

            buffer.Position = 0;
            var canonicalContentType = expectedFormat == "JPEG" ? "image/jpeg" : $"image/{expectedFormat.ToLowerInvariant()}";
            return new ValidatedUpload(
                filename,
                extension,
                canonicalContentType,
                image.Width,
                image.Height,
                buffer);
        }
        catch (InvalidDataException)
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    private static PropertyMediaItem ToItem(PropertyMedia media) => new(
        media.Id,
        media.OriginalFilename,
        media.MimeType,
        media.FileSize,
        media.Width,
        media.Height,
        media.DisplayOrder,
        media.UploadedAt,
        media.AnalysisStatus,
        media.GetAnalysis(),
        media.AnalysisAttemptCount,
        media.AnalysisLastError,
        media.WalkthroughStop,
        media.IsInGallery,
        media.SourceVideoId,
        media.SourceVideoTimestampMs,
        media.AppliedFixes,
        media.OriginalBlobPath != null,
        media.EditedAtUtc);

    private sealed record ValidatedUpload(
        string Filename,
        string Extension,
        string ContentType,
        int Width,
        int Height,
        MemoryStream Content);
}
