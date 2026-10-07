using ListingStudio.Application.Neighborhoods;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Neighborhoods;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Neighborhoods;

public sealed class NeighborhoodInsightService(
    ApplicationDbContext dbContext,
    INeighborhoodDataProvider provider,
    IPropertyMediaStorage storage,
    IOptions<NeighborhoodInsightsOptions> options,
    TimeProvider timeProvider) : INeighborhoodInsightService
{
    private readonly NeighborhoodInsightsOptions configuration = options.Value;

    public bool IsConfigured => provider.IsConfigured;

    public async Task<IReadOnlyList<NeighborhoodInsightResult>> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await Query(organizationId, propertyId)
            .OrderBy(insight => insight.Category)
            .ThenBy(insight => insight.DistanceMiles)
            .Select(insight => ToResult(insight))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NeighborhoodInsightResult>> RefreshAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (property is null)
        {
            return [];
        }

        if (property.IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot refresh neighborhood insights.");
        }

        var fullAddress = string.Join(", ", new[]
        {
            property.Address1,
            property.Address2,
            property.City,
            $"{property.State} {property.ZipCode}",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var candidates = await provider.SearchAsync(
            new NeighborhoodSearchRequest(fullAddress, configuration.RadiusMeters, configuration.MaximumResults),
            cancellationToken);

        var priorInsights = await Query(organizationId, propertyId)
            .AsNoTracking()
            .ToDictionaryAsync(insight => insight.ProviderPlaceId, StringComparer.Ordinal, cancellationToken);
        await Query(organizationId, propertyId).ExecuteDeleteAsync(cancellationToken);

        var checkedAt = timeProvider.GetUtcNow();
        foreach (var candidate in candidates)
        {
            var insight = NeighborhoodInsight.Create(
                organizationId,
                propertyId,
                candidate.ProviderPlaceId,
                candidate.Category,
                candidate.Name,
                candidate.Address,
                candidate.DistanceMiles,
                candidate.SourceUrl,
                candidate.HasPhoto,
                candidate.PhotoAttribution,
                candidate.PhotoAttributionUrl,
                candidate.PhotoSourceUrl,
                checkedAt);
            if (priorInsights.TryGetValue(candidate.ProviderPlaceId, out var prior))
            {
                insight.SetApproval(prior.IsApproved);
                insight.CopyVideoPhotoFrom(prior);
            }
            dbContext.NeighborhoodInsights.Add(insight);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var retainedPlaceIds = candidates.Select(candidate => candidate.ProviderPlaceId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in priorInsights.Values.Where(insight =>
                     !retainedPlaceIds.Contains(insight.ProviderPlaceId)
                     && insight.VideoPhotoBlobPath is not null))
        {
            await storage.DeleteAsync(removed.VideoPhotoBlobPath!, cancellationToken);
        }

        return await GetAsync(userId, propertyId, cancellationToken);
    }

    public async Task<bool> SetApprovalAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var insight = await Query(organizationId, propertyId)
            .SingleOrDefaultAsync(candidate => candidate.Id == insightId, cancellationToken);
        if (insight is null)
        {
            return false;
        }

        insight.SetApproval(approved);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<NeighborhoodPhoto?> OpenPhotoAsync(
        string userId,
        Guid insightId,
        CancellationToken cancellationToken = default)
    {
        if (!provider.IsConfigured)
        {
            return null;
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var placeId = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .Where(insight => insight.Id == insightId && insight.OrganizationId == organizationId && insight.HasPhoto)
            .Select(insight => insight.ProviderPlaceId)
            .SingleOrDefaultAsync(cancellationToken);
        return placeId is null ? null : await provider.OpenPhotoAsync(placeId, cancellationToken);
    }

    public async Task<bool> UploadVideoPhotoAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        NeighborhoodVideoPhotoUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var insight = await Query(organizationId, propertyId)
            .SingleOrDefaultAsync(candidate => candidate.Id == insightId, cancellationToken);
        if (insight is null)
        {
            return false;
        }

        if (!insight.IsApproved)
        {
            throw new InvalidOperationException(
                "Approve the nearby place before attaching a licensed video photo.");
        }

        var validated = await ValidateAndBufferAsync(upload, cancellationToken);
        await using var content = validated.Content;
        var blobPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/neighborhood/{insightId:N}/{Guid.NewGuid():N}{validated.Extension}";
        await storage.StoreAsync(blobPath, content, validated.ContentType, cancellationToken);
        var priorBlobPath = insight.VideoPhotoBlobPath;
        try
        {
            insight.SetVideoPhoto(
                blobPath,
                validated.Filename,
                validated.ContentType,
                content.Length,
                validated.Width,
                validated.Height,
                upload.Credit,
                timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(blobPath, CancellationToken.None);
            throw;
        }

        if (priorBlobPath is not null)
        {
            await storage.DeleteAsync(priorBlobPath, cancellationToken);
        }

        return true;
    }

    public async Task<bool> DeleteVideoPhotoAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var insight = await Query(organizationId, propertyId)
            .SingleOrDefaultAsync(candidate => candidate.Id == insightId, cancellationToken);
        if (insight is null || insight.VideoPhotoBlobPath is null)
        {
            return false;
        }

        var blobPath = insight.RemoveVideoPhoto()!;
        await dbContext.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(blobPath, cancellationToken);
        return true;
    }

    public async Task<NeighborhoodVideoPhotoContent?> OpenVideoPhotoAsync(
        string userId,
        Guid insightId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var asset = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .Where(insight => insight.Id == insightId && insight.OrganizationId == organizationId)
            .Select(insight => new
            {
                insight.VideoPhotoBlobPath,
                insight.VideoPhotoMimeType,
                insight.VideoPhotoFilename,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (asset?.VideoPhotoBlobPath is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(asset.VideoPhotoBlobPath, cancellationToken);
        return content is null
            ? null
            : new NeighborhoodVideoPhotoContent(
                content,
                asset.VideoPhotoMimeType!,
                asset.VideoPhotoFilename!);
    }

    private IQueryable<NeighborhoodInsight> Query(Guid organizationId, Guid propertyId) =>
        dbContext.NeighborhoodInsights.Where(insight =>
            insight.OrganizationId == organizationId && insight.PropertyId == propertyId);

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

    private static NeighborhoodInsightResult ToResult(NeighborhoodInsight insight) => new(
        insight.Id,
        insight.Category,
        insight.Name,
        insight.Address,
        insight.DistanceMiles,
        insight.SourceUrl,
        insight.HasPhoto,
        insight.PhotoAttribution,
        insight.PhotoAttributionUrl,
        insight.PhotoSourceUrl,
        insight.IsApproved,
        insight.CheckedAtUtc,
        insight.VideoPhotoBlobPath is not null,
        insight.VideoPhotoFilename,
        insight.VideoPhotoCredit);

    private static async Task<ValidatedVideoPhoto> ValidateAndBufferAsync(
        NeighborhoodVideoPhotoUpload upload,
        CancellationToken cancellationToken)
    {
        const long maximumFileSize = 20 * 1024 * 1024;
        const int maximumImageDimension = 15_000;
        const long maximumPixelCount = 50_000_000;
        var filename = Path.GetFileName(upload.OriginalFilename).Trim();
        if (filename.Length is 0 or > 255)
        {
            throw new InvalidDataException("The image filename is required and cannot exceed 255 characters.");
        }

        if (string.IsNullOrWhiteSpace(upload.Credit) || upload.Credit.Trim().Length > 300)
        {
            throw new InvalidDataException("A photo credit of 300 characters or fewer is required.");
        }

        if (upload.FileSize <= 0 || upload.FileSize > maximumFileSize)
        {
            throw new InvalidDataException("Neighborhood video photos must be between 1 byte and 20 MB.");
        }

        var extension = Path.GetExtension(filename).ToLowerInvariant();
        var expectedFormat = extension switch
        {
            ".jpg" or ".jpeg" => "JPEG",
            ".png" => "PNG",
            ".webp" => "WEBP",
            _ => throw new InvalidDataException("Only JPG, JPEG, PNG, and WEBP images are allowed."),
        };
        var actualFormat = upload.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "JPEG",
            "image/png" => "PNG",
            "image/webp" => "WEBP",
            _ => string.Empty,
        };
        if (!string.Equals(expectedFormat, actualFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The image MIME type does not match its filename extension.");
        }

        var buffer = new MemoryStream((int)Math.Min(upload.FileSize, 64 * 1024));
        var copyBuffer = new byte[64 * 1024];
        while (buffer.Length <= upload.FileSize)
        {
            var remaining = upload.FileSize - buffer.Length + 1;
            var read = await upload.Content.ReadAsync(
                copyBuffer.AsMemory(0, (int)Math.Min(copyBuffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
        }

        if (buffer.Length != upload.FileSize || buffer.Length > maximumFileSize)
        {
            await buffer.DisposeAsync();
            throw new InvalidDataException("The uploaded image size did not match the declared file size.");
        }

        try
        {
            var image = ListingStudio.Infrastructure.Properties.ImageMetadataReader.Read(
                buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            if (!string.Equals(image.Format, expectedFormat, StringComparison.OrdinalIgnoreCase)
                || image.Width > maximumImageDimension
                || image.Height > maximumImageDimension
                || (long)image.Width * image.Height > maximumPixelCount)
            {
                throw new InvalidDataException("The image format or dimensions are not supported.");
            }

            buffer.Position = 0;
            var contentType = expectedFormat == "JPEG" ? "image/jpeg" : $"image/{expectedFormat.ToLowerInvariant()}";
            return new ValidatedVideoPhoto(
                filename,
                extension,
                contentType,
                image.Width,
                image.Height,
                buffer);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    private sealed record ValidatedVideoPhoto(
        string Filename,
        string Extension,
        string ContentType,
        int Width,
        int Height,
        MemoryStream Content);
}
