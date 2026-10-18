using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// Photo fixes on the Media tab. A fix replaces the photo everywhere it is used and keeps the upload, so the agent
/// can compare the two and revert.
/// </summary>
public interface IPropertyPhotoFixService
{
    bool IsClutterRemovalEnabled { get; }

    /// <summary>The latest unfinished or failed fix of each photo of the property.</summary>
    Task<IReadOnlyList<PropertyPhotoFixItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a fix of the photo as it is now. Returns false when the photo is unavailable; throws
    /// <see cref="InvalidOperationException"/> when a fix of it is already running or the fix is not available.
    /// </summary>
    Task<bool> RequestAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        PhotoFix fix,
        CancellationToken cancellationToken = default);

    /// <summary>Puts the original upload back in place of the fixed photo.</summary>
    Task<bool> RevertAsync(
        string userId,
        Guid propertyId,
        Guid mediaId,
        CancellationToken cancellationToken = default);

    /// <summary>The upload as the agent added it, for a photo that has been fixed.</summary>
    Task<PropertyMediaContent?> OpenOriginalReadAsync(
        string userId,
        Guid mediaId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyPhotoFixItem(
    Guid PropertyMediaId,
    Guid FixId,
    PhotoFix Kind,
    PropertyPhotoFixStatus Status,
    string? Error,
    bool IsInProgress);

public interface IPropertyPhotoFixProcessor
{
    /// <summary>Makes the next requested fixed photo. Returns null when no fix is waiting.</summary>
    Task<PropertyPhotoFixRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed record PropertyPhotoFixRunResult(
    Guid FixId,
    Guid PropertyMediaId,
    PhotoFix Kind,
    bool Succeeded,
    bool WillRetry,
    int AttemptNumber,
    string? Error);
