using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// The optional AI drone intro on the Exterior front stop: a generated descent that lands on the real front photo.
/// The agent acknowledges it is AI-generated before asking for one, and must approve the finished clip before
/// campaign videos open with it.
/// </summary>
public interface IAerialIntroService
{
    bool IsEnabled { get; }

    /// <summary>The property's drone intro, or null when none was requested.</summary>
    Task<AerialIntroItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a drone intro for the property's current Exterior front photo, replacing any earlier one (approved or
    /// not). Requires <paramref name="disclosureAcknowledged"/>.
    /// </summary>
    Task<AerialIntroItem> RequestAsync(
        string userId,
        Guid propertyId,
        int heightFeet,
        bool disclosureAcknowledged,
        CancellationToken cancellationToken = default);

    /// <summary>Approves the finished, current intro for campaign videos. Returns false when there is none to approve.</summary>
    Task<bool> ApproveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>Changes how quickly the approved clip plays in previews and future campaign renders.</summary>
    Task<bool> SetPlaybackRateAsync(
        string userId,
        Guid propertyId,
        decimal playbackRate,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the drone intro and its clip. Returns false when the property has none.</summary>
    Task<bool> RemoveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<AerialIntroContent?> OpenReadAsync(
        string userId,
        Guid clipId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A property's drone intro. <see cref="IsCurrent"/> is false once the Exterior front photo it lands on was replaced,
/// fixed or moved, so the clip no longer matches the tour; such a clip is never used and can be made again.
/// </summary>
public sealed record AerialIntroItem(
    Guid Id,
    Guid PropertyMediaId,
    int HeightFeet,
    AerialIntroClipStatus Status,
    string? Error,
    bool CanRetry,
    DateTimeOffset? ApprovedAtUtc,
    bool IsCurrent,
    decimal PlaybackRate)
{
    public bool IsReady => Status == AerialIntroClipStatus.Completed;

    public bool IsApproved => ApprovedAtUtc is not null;

    public bool IsInProgress => Status is AerialIntroClipStatus.Pending
        or AerialIntroClipStatus.Submitting
        or AerialIntroClipStatus.Generating
        || CanRetry;

    /// <summary>Campaign videos open with the intro only when it is finished, approved and still matches the tour.</summary>
    public bool IsUsedInVideos => IsReady && IsApproved && IsCurrent;
}

public sealed record AerialIntroContent(Stream Content, string ContentType);

public interface IAerialIntroProcessor
{
    Task<AerialIntroRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed record AerialIntroRunResult(
    Guid ClipId,
    AerialIntroClipStatus Status,
    int AttemptNumber,
    string? Error);
