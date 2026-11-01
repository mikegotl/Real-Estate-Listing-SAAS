using ListingStudio.Application.Videos;

namespace ListingStudio.Video.Rendering;

/// <summary>
/// Supplies the Creative Listing Studio music library: original tracks played by sampled instruments, bundled with the
/// renderer (see src/ListingStudio.Video/Music/README.md for how they were made and their licenses). Each track starts
/// on its first beat at the tempo in <see cref="CampaignCreativeDirector.BeatsPerMinute"/>, so short-form beat cuts land
/// on the drums. The renderer loops and trims the file to the video's length.
/// </summary>
public sealed class OriginalCampaignSoundtrackService : ICampaignSoundtrackService
{
    public static string MusicDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Music");

    public async Task<VideoRenderMusicAsset?> MaterializeAsync(
        string? assetId,
        int durationMs,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (assetId is null)
        {
            return null;
        }

        var source = TrackPath(assetId)
            ?? throw new InvalidDataException("The selected soundtrack is not part of the Creative Listing Studio music library.");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The music library file is missing from the renderer.", source);
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "soundtrack-" + Path.GetFileName(source));
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        return new VideoRenderMusicAsset(assetId, path);
    }

    /// <summary>The bundled file for a library track, or null when the asset is not in the library.</summary>
    public static string? TrackPath(string assetId) => CampaignCreativeDirector.SoundtrackFileName(assetId) is { } file
        ? Path.Combine(MusicDirectory, file)
        : null;
}
