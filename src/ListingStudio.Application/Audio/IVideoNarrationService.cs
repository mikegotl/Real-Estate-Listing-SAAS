namespace ListingStudio.Application.Audio;

public interface IVideoNarrationService
{
    Task<VideoNarrationResult?> GetLatestAsync(
        string userId,
        Guid videoProductionPlanId,
        CancellationToken cancellationToken = default);

    Task<VideoNarrationResult?> GenerateAsync(
        string userId,
        Guid videoProductionPlanId,
        CancellationToken cancellationToken = default);

    Task<CampaignAssetContent?> OpenAudioAsync(
        string userId,
        Guid narrationId,
        CancellationToken cancellationToken = default);
}
