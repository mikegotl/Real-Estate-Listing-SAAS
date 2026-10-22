using ListingStudio.Application.Properties;

namespace ListingStudio.Infrastructure.Properties;

public sealed class PropertyMediaResetService(
    IPropertyMediaService mediaService,
    IPropertyVideoService videoService) : IPropertyMediaResetService
{
    public async Task<PropertyMediaResetResult> ResetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var videos = await videoService.ListAsync(userId, propertyId, cancellationToken);
        var media = await mediaService.ListAsync(userId, propertyId, cancellationToken);

        var deletedVideos = 0;
        foreach (var video in videos)
        {
            if (await videoService.DeleteAsync(userId, propertyId, video.Id, cancellationToken))
            {
                deletedVideos++;
            }
        }

        var deletedPhotos = 0;
        foreach (var photo in media)
        {
            if (await mediaService.DeleteAsync(userId, propertyId, photo.Id, cancellationToken))
            {
                deletedPhotos++;
            }
        }

        return new PropertyMediaResetResult(deletedPhotos, deletedVideos);
    }
}
