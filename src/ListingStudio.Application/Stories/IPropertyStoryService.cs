namespace ListingStudio.Application.Stories;

public interface IPropertyStoryService
{
    Task<PropertyStoryResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<PropertyStoryResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}
