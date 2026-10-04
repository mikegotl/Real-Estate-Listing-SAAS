using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

public interface IPropertyMediaService
{
    public const int MaximumMediaPerProperty = 50;
    public const long MaximumFileSize = 20 * 1024 * 1024;

    Task<IReadOnlyList<PropertyMediaItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyMediaUpload upload,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default);

    Task<bool> ReorderAsync(
        string userId,
        Guid propertyId,
        IReadOnlyList<Guid> orderedMediaIds,
        CancellationToken cancellationToken = default);

    Task<PropertyMediaContent?> OpenReadAsync(
        string userId,
        Guid mediaId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyMediaUpload(
    string OriginalFilename,
    string ContentType,
    long FileSize,
    Stream Content);

public sealed record PropertyMediaItem(
    Guid Id,
    string OriginalFilename,
    string MimeType,
    long FileSize,
    int Width,
    int Height,
    int DisplayOrder,
    DateTimeOffset UploadedAt,
    PropertyMediaAnalysisStatus AnalysisStatus);

public sealed record PropertyMediaContent(Stream Content, string MimeType, string OriginalFilename);
