using ListingStudio.Application.Audio;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface IVideoRenderer
{
    Task<VideoRenderResult> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VideoRenderMediaAsset(
    Guid PropertyMediaId,
    string FilePath,
    int Width,
    int Height);

public sealed record VideoRenderNarrationAsset(
    string FilePath,
    VoiceTimingMetadata? Timing);

public sealed record VideoRenderBrandAsset(
    string AssetId,
    string FilePath,
    int Width,
    int Height);

public sealed record VideoRenderMusicAsset(
    string AssetId,
    string FilePath);

/// <summary>
/// A generated clip. A clip that is <paramref name="FitToScene"/> (an AI walk-through used as a short-form shot)
/// skips its first <paramref name="LeadInMs"/>, which still look like the photo it starts from, and plays the rest
/// up to twice as fast so the whole walk fits the shot; other clips play from the start at normal speed.
/// </summary>
public sealed record VideoRenderGeneratedClipAsset(
    Guid GeneratedClipId,
    string FilePath,
    int Width,
    int Height,
    int DurationMs,
    int LeadInMs = 0,
    bool FitToScene = false);

public sealed record VideoRenderPropertyVideoAsset(
    Guid PropertyVideoId,
    string FilePath,
    int Width,
    int Height,
    int DurationMs);

public sealed record VideoRenderNeighborhoodAsset(
    Guid NeighborhoodInsightId,
    string FilePath,
    int Width,
    int Height,
    string Credit);

/// <summary>
/// A walk-through clip from one listing photo to the next. When two consecutive scenes show exactly these photos, the
/// renderer plays the clip at the end of the first scene so the camera walks into the second.
/// </summary>
public sealed record VideoRenderWalkthroughTransitionAsset(
    Guid FromPropertyMediaId,
    Guid ToPropertyMediaId,
    string FilePath,
    int DurationMs);

/// <summary>
/// The approved AI drone intro, which descends onto a listing photo. When the first scene shows that photo, the
/// renderer opens the video with the end of the clip (sped up to at most double speed when the scene is short) and
/// then holds the photo for the rest of the scene. The AI disclosure is already burned into the clip.
/// </summary>
public sealed record VideoRenderAerialIntroAsset(
    Guid PropertyMediaId,
    string FilePath,
    int DurationMs);

/// <summary>
/// A lip-synced agent presenter whose first and optional second source sections align with the campaign narration.
/// The renderer ignores the clip's audio and keeps the campaign's original narration mix.
/// </summary>
public sealed record VideoRenderAgentPresenterAsset(
    string AssetId,
    string FilePath,
    int Width,
    int Height,
    int IntroTimelineStartMs,
    int IntroDurationMs,
    int OutroTimelineStartMs,
    int OutroDurationMs);

public sealed record VideoRenderRequest(
    VideoProductionSpecification Specification,
    IReadOnlyList<VideoRenderMediaAsset> PropertyMedia,
    VideoRenderNarrationAsset? Narration,
    IReadOnlyList<VideoRenderBrandAsset> BrandAssets,
    VideoRenderMusicAsset? Music,
    string OutputFilePath,
    IReadOnlyList<VideoRenderGeneratedClipAsset>? GeneratedClips = null,
    IReadOnlyList<VideoRenderNeighborhoodAsset>? NeighborhoodAssets = null,
    IReadOnlyList<VideoRenderPropertyVideoAsset>? PropertyVideos = null,
    IReadOnlyList<VideoRenderWalkthroughTransitionAsset>? WalkthroughTransitions = null,
    VideoRenderAgentPresenterAsset? AgentPresenter = null,
    VideoRenderAerialIntroAsset? AerialIntro = null);

public sealed record VideoRenderResult(
    string OutputFilePath,
    int ExitCode,
    TimeSpan RenderDuration,
    string StandardOutput,
    string StandardError);

public sealed class VideoRenderException(
    string message,
    int? exitCode = null,
    string standardError = "",
    Exception? innerException = null) : Exception(message, innerException)
{
    public int? ExitCode { get; } = exitCode;

    public string StandardError { get; } = standardError;
}
