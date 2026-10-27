namespace ListingStudio.Application.Videos;

public interface IAgentAvatarVideoService
{
    bool IsEnabled { get; }

    Task<AgentAvatarVideoResult?> GetOrCreateAsync(
        Guid organizationId,
        Guid propertyId,
        string userId,
        Guid portraitId,
        Guid narrationId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentAvatarVideoResult(
    Guid Id,
    Guid VideoNarrationId,
    int IntroTimelineStartMs,
    int IntroDurationMs,
    int OutroTimelineStartMs,
    int OutroDurationMs,
    int Width,
    int Height,
    string Provider,
    string Model,
    decimal? EstimatedCostUsd,
    bool Reused);
