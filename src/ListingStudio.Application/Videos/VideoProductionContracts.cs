using ListingStudio.Application.Stories;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public sealed record VideoMediaInput(
    Guid MediaId,
    int Width,
    int Height,
    PropertyMediaObservation Analysis);

public sealed record VideoWalkthroughInput(
    Guid PropertyVideoId,
    string Filename,
    int Width,
    int Height,
    int DurationMs);

public sealed record AcceptedNarrationScriptSegment(string Key, string Text);

public sealed record AcceptedNarrationScriptInput(
    Guid ScriptId,
    DateTimeOffset AcceptedAtUtc,
    IReadOnlyList<AcceptedNarrationScriptSegment> Segments);

public sealed record VideoPropertyStoryInput(
    Guid Id,
    int Version,
    PropertyStoryContentSnapshot Content);

public sealed record PropertyStoryContentSnapshot(
    string CampaignTitle,
    string OpeningHook,
    string PropertyNarrative,
    IReadOnlyList<string> Highlights,
    string VoiceoverScript,
    string ClosingCta);

public sealed record VideoDirectionRequest(
    Guid PropertyId,
    VideoPropertyStoryInput PropertyStory,
    VerifiedPropertyData VerifiedProperty,
    IReadOnlyList<VideoMediaInput> Media,
    RequestedDuration RequestedDuration,
    VideoAspectRatio AspectRatio,
    VideoOutputProfile Output,
    NormalizedRect SafeZone,
    IReadOnlyList<FactBinding> FactBindings,
    BrandKit Brand,
    GroundedText CallToAction,
    IReadOnlySet<Guid> ApprovedGeneratedClipIds,
    IReadOnlySet<string> ApprovedBrandAssetIds,
    IReadOnlySet<string> ApprovedMusicAssetIds,
    AcceptedNarrationScriptInput? AcceptedNarrationScript = null,
    IReadOnlyList<VideoWalkthroughInput>? WalkthroughVideos = null);

public sealed record DirectedEditorialPlan(AudioPlan Audio, IReadOnlyList<VideoScene> Scenes);

public sealed record VideoSpecificationValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static VideoSpecificationValidationResult Success { get; } = new(true, []);
}

public sealed record VideoProductionPlanResult(
    Guid Id,
    Guid PropertyId,
    Guid PropertyStoryId,
    int Version,
    string DirectorVersion,
    VideoProductionSpecification Specification,
    DateTimeOffset CreatedAtUtc,
    bool Reused);
