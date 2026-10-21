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
    TimeProvider timeProvider) : IAgentPortraitService
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
    };

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

        var encoded = await imageCodec.EncodeJpegAsync(decoded, cancellationToken);
        var portraitId = Guid.NewGuid();
        var path = $"organizations/{organizationId:N}/agent-portraits/{portraitId:N}.jpg";
        await using var content = new MemoryStream(encoded, writable: false);
        await storage.StoreAsync(path, content, "image/jpeg", cancellationToken);
        try
        {
            var portrait = AgentPortrait.Create(
                organizationId,
                userId,
                path,
                SafeFilename(upload.OriginalFilename),
                "image/jpeg",
                encoded.LongLength,
                decoded.Width,
                decoded.Height,
                timeProvider.GetUtcNow());
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

    public async Task<AgentPortraitContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var portrait = await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == portraitId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(portrait.BlobPath, cancellationToken);
        return content is null ? null : new AgentPortraitContent(content, portrait.MimeType);
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

    private static AgentPortraitItem ToItem(AgentPortrait portrait) => new(
        portrait.Id,
        portrait.OriginalFilename,
        portrait.Width,
        portrait.Height,
        portrait.UpdatedAtUtc);
}
