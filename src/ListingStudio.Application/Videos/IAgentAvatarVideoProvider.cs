namespace ListingStudio.Application.Videos;

public interface IAgentAvatarVideoProvider
{
    bool IsEnabled { get; }

    string GenerationVersion { get; }

    int MaximumIntroDurationMs { get; }

    int MaximumOutroDurationMs { get; }

    Task<AgentAvatarVideoProviderResult> GenerateAsync(
        AgentAvatarVideoProviderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentAvatarVideoProviderRequest(
    string PortraitFilename,
    string PortraitContentType,
    Stream Portrait,
    string AudioFilename,
    string AudioContentType,
    Stream Audio,
    int DurationMs,
    string IdempotencyKey);

public sealed record AgentAvatarVideoProviderResult(
    Stream Content,
    string ContentType,
    string Provider,
    string Model,
    string? ProviderRequestId,
    int DurationMs,
    int Width,
    int Height,
    decimal? EstimatedCostUsd,
    IReadOnlyDictionary<string, string> Metadata);

public interface IAgentAvatarNarrationExcerptRenderer
{
    Task<AgentAvatarNarrationExcerpt> RenderAsync(
        Stream narration,
        string contentType,
        IReadOnlyList<AgentAvatarNarrationExcerptPart> parts,
        CancellationToken cancellationToken = default);
}

public sealed record AgentAvatarNarrationExcerptPart(int StartMs, int DurationMs);

public sealed record AgentAvatarNarrationExcerpt(
    byte[] AudioData,
    string ContentType,
    string FileExtension,
    int DurationMs);
