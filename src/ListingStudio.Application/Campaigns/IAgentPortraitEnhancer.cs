namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Applies a restrained professional-headshot edit while preserving the person's identity, clothing, pose and body.
/// The implementation is an outbound adapter because the application layer must not depend on an AI provider.
/// </summary>
public interface IAgentPortraitEnhancer
{
    bool IsEnabled { get; }

    Task<byte[]> EnhanceAsync(
        AgentPortraitEnhancementRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentPortraitEnhancementRequest(
    byte[] Image,
    string ContentType,
    int Width,
    int Height);
