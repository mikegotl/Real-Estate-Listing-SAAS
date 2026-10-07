using System.Data;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class PropertyVideoService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPropertyVideoTranscoder transcoder) : IPropertyVideoService
{
    private static readonly Dictionary<string, string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "mp4",
        [".m4v"] = "mp4",
        [".mov"] = "quicktime",
        [".webm"] = "webm",
    };

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = "mp4",
        ["video/x-m4v"] = "mp4",
        ["video/quicktime"] = "quicktime",
        ["video/webm"] = "webm",
    };

    public async Task<IReadOnlyList<PropertyVideoItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: true, cancellationToken))
        {
            return [];
        }

        return await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && video.PropertyId == propertyId)
            .OrderByDescending(video => video.UploadedAtUtc)
            .Select(video => ToItem(video))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyVideoUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var validated = await ValidateAndSpoolAsync(upload, cancellationToken);
        try
        {
            var metadata = await transcoder.ProbeAsync(
                validated.Content,
                validated.Filename,
                cancellationToken);
            ValidateMetadata(metadata, validated.ContainerKind);
            validated.Content.Position = 0;

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
            {
                throw new InvalidOperationException("The property is unavailable for video uploads.");
            }

            var existingCount = await dbContext.PropertyVideos.CountAsync(
                video => video.OrganizationId == organizationId && video.PropertyId == propertyId,
                cancellationToken);
            if (existingCount >= IPropertyVideoService.MaximumVideosPerProperty)
            {
                throw new InvalidOperationException(
                    $"A property can have at most {IPropertyVideoService.MaximumVideosPerProperty} videos.");
            }

            var videoId = Guid.NewGuid();
            var blobPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/videos/{videoId:N}/original{validated.Extension}";
            var video = PropertyVideo.Create(
                organizationId,
                propertyId,
                blobPath,
                validated.Filename,
                validated.ContentType,
                upload.FileSize,
                metadata.Width,
                metadata.Height,
                metadata.DurationMs,
                metadata.FrameRate,
                metadata.HasAudio);

            await storage.StoreAsync(blobPath, validated.Content, validated.ContentType, cancellationToken);
            try
            {
                dbContext.PropertyVideos.Add(video);
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
                        "Property video persistence failed and the stored object could not be cleaned up.",
                        persistenceError,
                        cleanupError);
                }

                throw;
            }

            return video.Id;
        }
        finally
        {
            await validated.Content.DisposeAsync();
            File.Delete(validated.TemporaryPath);
        }
    }

    public async Task<PropertyVideoContent?> OpenReadAsync(
        string userId,
        Guid videoId,
        bool enhanced,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var video = await dbContext.PropertyVideos
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == videoId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (video is null || (enhanced && video.ProcessingStatus != PropertyVideoProcessingStatus.Completed))
        {
            return null;
        }

        var path = enhanced ? video.EnhancedBlobPath! : video.OriginalBlobPath;
        var content = await storage.OpenReadAsync(path, cancellationToken);
        if (content is null)
        {
            return null;
        }

        var filename = enhanced
            ? $"{Path.GetFileNameWithoutExtension(video.OriginalFilename)}-enhanced.mp4"
            : video.OriginalFilename;
        return new PropertyVideoContent(content, enhanced ? "video/mp4" : video.OriginalMimeType, filename);
    }

    public async Task<bool> RetryProcessingAsync(
        string userId,
        Guid propertyId,
        Guid videoId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var video = await dbContext.PropertyVideos.SingleOrDefaultAsync(
            candidate => candidate.Id == videoId
                && candidate.PropertyId == propertyId
                && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (video is null || video.ProcessingStatus != PropertyVideoProcessingStatus.Failed)
        {
            return false;
        }

        video.QueueRetry();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid videoId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await PropertyExistsAsync(organizationId, propertyId, includeArchived: false, cancellationToken))
        {
            return false;
        }

        var video = await dbContext.PropertyVideos.SingleOrDefaultAsync(
            candidate => candidate.Id == videoId
                && candidate.PropertyId == propertyId
                && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (video is null)
        {
            return false;
        }

        await storage.DeleteAsync(video.OriginalBlobPath, cancellationToken);
        if (video.EnhancedBlobPath is not null)
        {
            await storage.DeleteAsync(video.EnhancedBlobPath, cancellationToken);
        }

        dbContext.PropertyVideos.Remove(video);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<ValidatedVideoUpload> ValidateAndSpoolAsync(
        PropertyVideoUpload upload,
        CancellationToken cancellationToken)
    {
        var filename = Path.GetFileName(upload.OriginalFilename).Trim();
        if (filename.Length is 0 or > 255)
        {
            throw new InvalidDataException("The original filename is required and cannot exceed 255 characters.");
        }

        if (upload.FileSize <= 0 || upload.FileSize > IPropertyVideoService.MaximumFileSize)
        {
            throw new InvalidDataException(
                $"Videos must be between 1 byte and {IPropertyVideoService.MaximumFileSize / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var extensionKind)
            || !AllowedContentTypes.TryGetValue(upload.ContentType, out var contentTypeKind)
            || !string.Equals(extensionKind, contentTypeKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only MP4, M4V, MOV, and WEBM videos with matching MIME types are allowed.");
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"listing-studio-upload-{Guid.NewGuid():N}{extension}");
        FileStream? content = null;
        try
        {
            content = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81_920];
            while (content.Length <= upload.FileSize)
            {
                var remaining = upload.FileSize - content.Length + 1;
                var bytesRead = await upload.Content.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            if (content.Length != upload.FileSize || content.Length > IPropertyVideoService.MaximumFileSize)
            {
                await content.DisposeAsync();
                throw new InvalidDataException("The uploaded video size did not match the declared file size.");
            }

            content.Position = 0;
            var canonicalContentType = extensionKind switch
            {
                "quicktime" => "video/quicktime",
                "webm" => "video/webm",
                _ => "video/mp4",
            };
            return new ValidatedVideoUpload(
                filename,
                extension,
                extensionKind,
                canonicalContentType,
                temporaryPath,
                content);
        }
        catch
        {
            if (content is not null)
            {
                await content.DisposeAsync();
            }

            File.Delete(temporaryPath);
            throw;
        }
    }

    private static void ValidateMetadata(PropertyVideoMetadata metadata, string expectedContainerKind)
    {
        var containerMatches = expectedContainerKind switch
        {
            "webm" => metadata.ContainerFormat.Contains("webm", StringComparison.OrdinalIgnoreCase),
            _ => metadata.ContainerFormat.Contains("mov", StringComparison.OrdinalIgnoreCase)
                || metadata.ContainerFormat.Contains("mp4", StringComparison.OrdinalIgnoreCase),
        };
        if (!containerMatches)
        {
            throw new InvalidDataException("The file content does not match its video filename and MIME type.");
        }

        if (metadata.DurationMs <= 0 || metadata.DurationMs > IPropertyVideoService.MaximumDurationMs)
        {
            throw new InvalidDataException(
                $"Videos must be no longer than {IPropertyVideoService.MaximumDurationMs / 60_000} minutes.");
        }

        if (metadata.Width <= 0 || metadata.Height <= 0
            || metadata.Width > IPropertyVideoService.MaximumDimension
            || metadata.Height > IPropertyVideoService.MaximumDimension)
        {
            throw new InvalidDataException(
                $"Video dimensions cannot exceed {IPropertyVideoService.MaximumDimension} pixels on either side.");
        }

        if (metadata.FrameRate is <= 0 or > 240)
        {
            throw new InvalidDataException("The video frame rate is invalid or unsupported.");
        }
    }

    private Task<bool> PropertyExistsAsync(
        Guid organizationId,
        Guid propertyId,
        bool includeArchived,
        CancellationToken cancellationToken) => dbContext.Properties.AnyAsync(
            property => property.Id == propertyId
                && property.OrganizationId == organizationId
                && (includeArchived || property.ArchivedAtUtc == null),
            cancellationToken);

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

    private static PropertyVideoItem ToItem(PropertyVideo video) => new(
        video.Id,
        video.OriginalFilename,
        video.OriginalMimeType,
        video.OriginalFileSize,
        video.Width,
        video.Height,
        video.DurationMs,
        video.FrameRate,
        video.HasAudio,
        video.UploadedAtUtc,
        video.ProcessingStatus,
        video.ProcessingAttemptCount,
        video.ProcessingLastError,
        video.EnhancedFileSize,
        video.EnhancedWidth,
        video.EnhancedHeight,
        video.EnhancedDurationMs,
        video.EnhancedFrameRate,
        video.EnhancementVersion);

    private sealed record ValidatedVideoUpload(
        string Filename,
        string Extension,
        string ContainerKind,
        string ContentType,
        string TemporaryPath,
        FileStream Content);
}
