namespace ListingStudio.Application.Audio;

public sealed record VoiceNarrationSegment(
    string Id,
    string Text,
    int PlannedStartMs,
    int PlannedDurationMs);

public sealed record VoiceGenerationRequest(IReadOnlyList<VoiceNarrationSegment> Segments);

public sealed record VoiceCharacterTiming(string Character, int StartMs, int EndMs);

public sealed record VoiceSegmentTiming(string SegmentId, int StartMs, int EndMs);

public sealed record VoiceTimingMetadata(
    IReadOnlyList<VoiceCharacterTiming> Characters,
    IReadOnlyList<VoiceSegmentTiming> Segments);

public sealed record VoiceGenerationResult(
    byte[] AudioData,
    string ContentType,
    string FileExtension,
    int DurationMs,
    VoiceTimingMetadata? Timing);

public sealed record VideoNarrationResult(
    Guid Id,
    Guid VideoProductionPlanId,
    int Version,
    string GenerationVersion,
    string ContentType,
    int DurationMs,
    VoiceTimingMetadata? Timing,
    DateTimeOffset CreatedAtUtc,
    bool Reused);

public sealed record CampaignAssetContent(Stream Content, string ContentType);
