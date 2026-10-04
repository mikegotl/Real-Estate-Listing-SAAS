using System.Data;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class PropertyMediaService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    ICampaignAssetStorage campaignAssetStorage) : IPropertyMediaService
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

        await storage.StoreAsync(blobPath, bufferedContent, validated.ContentType, cancellationToken);
        try
        {
            dbContext.PropertyMedia.Add(media);
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

        await storage.DeleteAsync(media.BlobPath, cancellationToken);
        dbContext.GeneratedVideoClips.RemoveRange(generatedClips);
        dbContext.PropertyMedia.Remove(media);
        await dbContext.SaveChangesAsync(cancellationToken);
        await NormalizeDisplayOrderAsync(organizationId, propertyId, cancellationToken);
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

        if (orderedMediaIds.Count != media.Count
            || orderedMediaIds.Distinct().Count() != media.Count
            || media.Any(candidate => !orderedMediaIds.Contains(candidate.Id)))
        {
            throw new ArgumentException("The order must include each property image exactly once.", nameof(orderedMediaIds));
        }

        var byId = media.ToDictionary(candidate => candidate.Id);
        for (var index = 0; index < orderedMediaIds.Count; index++)
        {
            byId[orderedMediaIds[index]].SetDisplayOrder(index);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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

        var buffer = new MemoryStream((int)upload.FileSize);
        await upload.Content.CopyToAsync(buffer, cancellationToken);
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
        media.AnalysisLastError);

    private sealed record ValidatedUpload(
        string Filename,
        string Extension,
        string ContentType,
        int Width,
        int Height,
        MemoryStream Content);
}
