using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class AgentAvatarPreviewService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IAgentAvatarPreviewRenderer renderer) : IAgentAvatarPreviewService
{
    public async Task<bool> PrepareAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var portrait = await FindOwnedAsync(userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return false;
        }

        var previewPath = PreviewPath(portrait);
        await using (var cached = await campaignAssetStorage.OpenReadAsync(previewPath, cancellationToken))
        {
            if (cached is not null)
            {
                return true;
            }
        }

        byte[] source;
        await using (var content = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The agent headshot could not be opened."))
        using (var buffer = new MemoryStream())
        {
            await content.CopyToAsync(buffer, cancellationToken);
            source = buffer.ToArray();
        }

        var preview = await renderer.RenderAsync(source, portrait.MimeType, cancellationToken);
        await using var previewStream = new MemoryStream(preview, writable: false);
        await campaignAssetStorage.StoreAsync(previewPath, previewStream, "video/mp4", cancellationToken);
        return true;
    }

    public async Task<AgentAvatarPreviewContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var portrait = await FindOwnedAsync(userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        var content = await campaignAssetStorage.OpenReadAsync(PreviewPath(portrait), cancellationToken);
        return content is null ? null : new AgentAvatarPreviewContent(content, "video/mp4");
    }

    private async Task<AgentPortrait?> FindOwnedAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (organizationId is null)
        {
            return null;
        }

        return await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            portrait => portrait.Id == portraitId
                && portrait.OrganizationId == organizationId
                && portrait.UserId == userId,
            cancellationToken);
    }

    private static string PreviewPath(AgentPortrait portrait) =>
        $"organizations/{portrait.OrganizationId:N}/agent-avatar-previews/{portrait.Id:N}.mp4";
}
