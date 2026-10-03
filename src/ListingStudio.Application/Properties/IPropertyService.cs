namespace ListingStudio.Application.Properties;

public interface IPropertyService
{
    Task<IReadOnlyList<PropertySummary>> ListAsync(
        string userId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<PropertyDetailsResult?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<Guid> CreateAsync(
        string userId,
        PropertyInput input,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        string userId,
        Guid propertyId,
        PropertyInput input,
        CancellationToken cancellationToken = default);

    Task<bool> ArchiveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}
