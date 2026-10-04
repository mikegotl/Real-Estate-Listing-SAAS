namespace ListingStudio.Application.Audio;

public interface ICampaignAssetStorage
{
    Task StoreAsync(
        string assetPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string assetPath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string assetPath, CancellationToken cancellationToken = default);
}
