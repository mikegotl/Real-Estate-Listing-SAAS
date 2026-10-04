using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface IVideoProductionPlanService
{
    Task<VideoProductionPlanResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default);

    Task<VideoProductionPlanResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default);
}
