namespace ListingStudio.Application.Videos;

public interface IAgentAvatarVideoProvider
{
    bool IsEnabled { get; }

    string GenerationVersion { get; }

    int MaximumIntroDurationMs { get; }

    int MaximumOutroDurationMs { get; }

    /// <summary>
    /// The longest presenter clip for a video the agent guides from start to finish (the whole voiceover lip-synced);
    /// 0 when the provider only makes short opening and closing clips.
    /// </summary>
    int MaximumGuideDurationMs => 0;

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

/// <summary>A stretch of the narration audio, after <paramref name="SilenceBeforeMs"/> of quiet.</summary>
public sealed record AgentAvatarNarrationExcerptPart(int StartMs, int DurationMs, int SilenceBeforeMs = 0);

public sealed record AgentAvatarNarrationExcerpt(
    byte[] AudioData,
    string ContentType,
    string FileExtension,
    int DurationMs);
