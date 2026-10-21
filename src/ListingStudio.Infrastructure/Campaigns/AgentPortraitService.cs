using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class AgentPortraitService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPhotoImageCodec imageCodec,
    IAgentPortraitEnhancer enhancer,
    TimeProvider timeProvider) : IAgentPortraitService
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
    };

    public bool IsAiFixEnabled => enhancer.IsEnabled;

    public async Task<AgentPortraitItem?> GetLatestAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var portrait = await dbContext.AgentPortraits.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.UserId == userId)
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return portrait is null ? null : ToItem(portrait);
    }

    public async Task<AgentPortraitItem> UploadAsync(
        string userId,
        AgentPortraitUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);
        if (upload.FileSize is <= 0 or > IAgentPortraitService.MaximumFileSize
            || !AllowedContentTypes.Contains(upload.ContentType))
        {
            throw new InvalidDataException("Choose a JPEG, PNG, or WebP agent photo no larger than 10 MB.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var bytes = await ReadBoundedAsync(upload.Content, IAgentPortraitService.MaximumFileSize, cancellationToken);
        PhotoPixels decoded;
        try
        {
            decoded = await imageCodec.DecodeAsync(
                bytes,
                NormalizeContentType(upload.ContentType),
                2_048,
                2_048,
                PhotoScaling.ShrinkToFit,
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException("The agent photo could not be read as a supported image.", exception);
        }

        return await StoreAsync(
            organizationId,
            userId,
            SafeFilename(upload.OriginalFilename),
            decoded,
            cancellationToken);
    }

    public async Task<AgentPortraitItem?> CropAsync(
        string userId,
        Guid portraitId,
        AgentPortraitCrop crop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crop);
        crop.Validate();
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var portrait = await FindOwnedAsync(organizationId, userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        var bytes = await ReadPortraitAsync(portrait, cancellationToken);
        var decoded = await imageCodec.DecodeAsync(
            bytes,
            portrait.MimeType,
            2_048,
            2_048,
            PhotoScaling.ShrinkToFit,
            cancellationToken);
        var cropped = Crop(decoded, crop);
        return await StoreAsync(
            organizationId,
            userId,
            DerivedFilename(portrait.OriginalFilename, "cropped"),
            cropped,
            cancellationToken,
            portrait.UpdatedAtUtc);
    }

    public async Task<AgentPortraitItem?> AiFixAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        if (!enhancer.IsEnabled)
        {
            throw new InvalidOperationException("AI headshot fix is not configured for this workspace.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var portrait = await FindOwnedAsync(organizationId, userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        var bytes = await ReadPortraitAsync(portrait, cancellationToken);
        var enhanced = await enhancer.EnhanceAsync(
            new AgentPortraitEnhancementRequest(bytes, portrait.MimeType, portrait.Width, portrait.Height),
            cancellationToken);
        PhotoPixels decoded;
        try
        {
            decoded = await imageCodec.DecodeAsync(
                enhanced,
                "image/png",
                2_048,
                2_048,
                PhotoScaling.ShrinkToFit,
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException("The AI provider returned an unreadable headshot.", exception);
        }

        return await StoreAsync(
            organizationId,
            userId,
            DerivedFilename(portrait.OriginalFilename, "ai-fixed"),
            decoded,
            cancellationToken,
            portrait.UpdatedAtUtc);
    }

    public async Task<AgentPortraitContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var portrait = await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == portraitId
                && candidate.OrganizationId == organizationId
                && candidate.UserId == userId,
            cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(portrait.BlobPath, cancellationToken);
        return content is null ? null : new AgentPortraitContent(content, portrait.MimeType);
    }

    private async Task<AgentPortraitItem> StoreAsync(
        Guid organizationId,
        string userId,
        string filename,
        PhotoPixels pixels,
        CancellationToken cancellationToken,
        DateTimeOffset? previousVersionAtUtc = null)
    {
        var encoded = await imageCodec.EncodeJpegAsync(pixels, cancellationToken);
        var portraitId = Guid.NewGuid();
        var path = $"organizations/{organizationId:N}/agent-portraits/{portraitId:N}.jpg";
        await using var content = new MemoryStream(encoded, writable: false);
        await storage.StoreAsync(path, content, "image/jpeg", cancellationToken);
        try
        {
            var updatedAtUtc = timeProvider.GetUtcNow();
            if (previousVersionAtUtc is { } previous && updatedAtUtc <= previous)
            {
                // PostgreSQL timestamps have microsecond precision; a millisecond remains ordered after round-trip.
                updatedAtUtc = previous.AddMilliseconds(1);
            }

            var portrait = AgentPortrait.Create(
                organizationId,
                userId,
                path,
                filename,
                "image/jpeg",
                encoded.LongLength,
                pixels.Width,
                pixels.Height,
                updatedAtUtc);
            dbContext.AgentPortraits.Add(portrait);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToItem(portrait);
        }
        catch
        {
            await storage.DeleteAsync(path, CancellationToken.None);
            throw;
        }
    }

    private Task<AgentPortrait?> FindOwnedAsync(
        Guid organizationId,
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken) =>
        dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == portraitId
                && candidate.OrganizationId == organizationId
                && candidate.UserId == userId,
            cancellationToken);

    private async Task<byte[]> ReadPortraitAsync(AgentPortrait portrait, CancellationToken cancellationToken)
    {
        await using var source = await storage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The agent headshot could not be opened.");
        return await ReadBoundedAsync(source, IAgentPortraitService.MaximumFileSize, cancellationToken);
    }

    private static PhotoPixels Crop(PhotoPixels source, AgentPortraitCrop request)
    {
        var targetRatio = request.Shape == AgentPortraitCropShape.Square ? 1m : 4m / 5m;
        var sourceRatio = source.Width / (decimal)source.Height;
        var baseWidth = sourceRatio >= targetRatio
            ? (decimal)source.Height * targetRatio
            : source.Width;
        var baseHeight = sourceRatio >= targetRatio
            ? source.Height
            : source.Width / targetRatio;
        var width = Math.Max(1, (int)Math.Floor(baseWidth / request.Zoom));
        var height = Math.Max(1, (int)Math.Floor(baseHeight / request.Zoom));
        var availableX = source.Width - width;
        var availableY = source.Height - height;
        var left = Math.Clamp((int)Math.Round(availableX * request.FocusX), 0, availableX);
        var top = Math.Clamp((int)Math.Round(availableY * request.FocusY), 0, availableY);
        var output = new byte[checked(width * height * 3)];
        var sourceStride = source.Width * 3;
        var outputStride = width * 3;
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source.Rgb,
                (top + row) * sourceStride + left * 3,
                output,
                row * outputStride,
                outputStride);
        }

        return new PhotoPixels(width, height, output);
    }

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The agent photo is larger than 10 MB.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static string NormalizeContentType(string contentType) =>
        string.Equals(contentType, "image/jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : contentType.ToLowerInvariant();

    private static string SafeFilename(string filename)
    {
        var name = Path.GetFileName(filename?.Trim());
        return string.IsNullOrWhiteSpace(name) ? "agent-photo.jpg" : name[..Math.Min(name.Length, 255)];
    }

    private static string DerivedFilename(string filename, string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(SafeFilename(filename));
        return $"{name[..Math.Min(name.Length, 230)]}-{suffix}.jpg";
    }

    private static AgentPortraitItem ToItem(AgentPortrait portrait) => new(
        portrait.Id,
        portrait.OriginalFilename,
        portrait.Width,
        portrait.Height,
        portrait.UpdatedAtUtc);
}
