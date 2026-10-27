namespace ListingStudio.Application.Campaigns;

public interface IAgentProfileService
{
    Task<AgentProfilePreferences> GetAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AgentProfilePreferences> SetAvatarDefaultAsync(
        string userId,
        bool includeAvatarInCampaigns,
        CancellationToken cancellationToken = default);
}

public sealed record AgentProfilePreferences(bool IncludeAvatarInCampaigns);

public interface IAgentAvatarPreviewService
{
    Task<bool> PrepareAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default);

    Task<AgentAvatarPreviewContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default);
}

public interface IAgentAvatarPreviewRenderer
{
    Task<byte[]> RenderAsync(
        byte[] portrait,
        string contentType,
        CancellationToken cancellationToken = default);
}

public sealed record AgentAvatarPreviewContent(Stream Content, string ContentType);
