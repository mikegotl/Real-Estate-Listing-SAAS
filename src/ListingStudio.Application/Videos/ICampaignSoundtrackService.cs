namespace ListingStudio.Application.Videos;

public interface ICampaignSoundtrackService
{
    Task<VideoRenderMusicAsset?> MaterializeAsync(
        string? assetId,
        int durationMs,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
