using ListingStudio.Domain.Neighborhoods;

namespace ListingStudio.Application.Neighborhoods;

public sealed record NeighborhoodSearchRequest(string FullAddress, int RadiusMeters, int MaximumResults);

public sealed record NeighborhoodPlaceCandidate(
    string ProviderPlaceId,
    NeighborhoodPlaceCategory Category,
    string Name,
    string Address,
    decimal DistanceMiles,
    string SourceUrl,
    bool HasPhoto,
    string? PhotoAttribution,
    string? PhotoAttributionUrl,
    string? PhotoSourceUrl);

public sealed record NeighborhoodInsightResult(
    Guid Id,
    NeighborhoodPlaceCategory Category,
    string Name,
    string Address,
    decimal DistanceMiles,
    string SourceUrl,
    bool HasPhoto,
    string? PhotoAttribution,
    string? PhotoAttributionUrl,
    string? PhotoSourceUrl,
    bool IsApproved,
    DateTimeOffset CheckedAtUtc,
    bool HasVideoPhoto,
    string? VideoPhotoFilename,
    string? VideoPhotoCredit);

public sealed record NeighborhoodPhoto(Stream Content, string ContentType);

public sealed record NeighborhoodVideoPhotoUpload(
    string OriginalFilename,
    string ContentType,
    long FileSize,
    string Credit,
    Stream Content);

public sealed record NeighborhoodVideoPhotoContent(
    Stream Content,
    string ContentType,
    string Filename);

public interface INeighborhoodDataProvider
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<NeighborhoodPlaceCandidate>> SearchAsync(
        NeighborhoodSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<NeighborhoodPhoto?> OpenPhotoAsync(
        string providerPlaceId,
        CancellationToken cancellationToken = default);
}

public interface INeighborhoodInsightService
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<NeighborhoodInsightResult>> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NeighborhoodInsightResult>> RefreshAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<bool> SetApprovalAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<NeighborhoodPhoto?> OpenPhotoAsync(
        string userId,
        Guid insightId,
        CancellationToken cancellationToken = default);

    Task<bool> UploadVideoPhotoAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        NeighborhoodVideoPhotoUpload upload,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteVideoPhotoAsync(
        string userId,
        Guid propertyId,
        Guid insightId,
        CancellationToken cancellationToken = default);

    Task<NeighborhoodVideoPhotoContent?> OpenVideoPhotoAsync(
        string userId,
        Guid insightId,
        CancellationToken cancellationToken = default);
}
