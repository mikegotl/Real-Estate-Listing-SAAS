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
    /// <summary>
    /// Makes the presenter preview for an owned headshot: a short lip-synced greeting when the presenter provider
    /// is enabled and the plan includes it, otherwise the free silent layout preview. Returns null when the
    /// headshot is not the user's.
    /// </summary>
    Task<AgentAvatarPreviewResult?> PrepareAsync(
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

/// <param name="LipSynced">True for a talking, lip-synced preview; false for the silent layout preview.</param>
/// <param name="FallbackReason">Why a talking preview was wanted but could not be made, for the user.</param>
public sealed record AgentAvatarPreviewResult(bool LipSynced, string? FallbackReason = null);
