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

public sealed record VideoRenderGeneratedClipAsset(
    Guid GeneratedClipId,
    string FilePath,
    int Width,
    int Height,
    int DurationMs);

public sealed record VideoRenderRequest(
    VideoProductionSpecification Specification,
    IReadOnlyList<VideoRenderMediaAsset> PropertyMedia,
    VideoRenderNarrationAsset? Narration,
    IReadOnlyList<VideoRenderBrandAsset> BrandAssets,
    VideoRenderMusicAsset? Music,
    string OutputFilePath,
    IReadOnlyList<VideoRenderGeneratedClipAsset>? GeneratedClips = null);

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
