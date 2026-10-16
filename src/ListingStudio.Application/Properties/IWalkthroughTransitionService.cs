using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>Walk-through clips between consecutive storyboard shots.</summary>
public interface IWalkthroughTransitionService
{
    bool IsEnabled { get; }

    /// <summary>One entry per pair of consecutive photos in the current storyboard order.</summary>
    Task<IReadOnlyList<WalkthroughTransitionItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a clip for every consecutive pair that has no clip for its current walking direction yet, and requeues
    /// pairs whose clip failed for good. Returns how many pairs were queued.
    /// </summary>
    Task<int> QueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<WalkthroughTransitionContent?> OpenReadAsync(
        string userId,
        Guid transitionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A consecutive shot pair and its clip. <see cref="IsCurrent"/> is false when the clip shown was made before the
/// latest generation settings or walking direction, so a new clip can be requested to replace it.
/// </summary>
public sealed record WalkthroughTransitionItem(
    Guid FromPropertyMediaId,
    Guid ToPropertyMediaId,
    Guid? TransitionId,
    WalkthroughTransitionStatus? Status,
    string? Error,
    bool IsCurrent = true)
{
    public bool IsReady => Status == WalkthroughTransitionStatus.Completed && TransitionId is not null;

    public bool IsInProgress => Status is WalkthroughTransitionStatus.Pending
        or WalkthroughTransitionStatus.Submitting
        or WalkthroughTransitionStatus.Generating;
}

public sealed record WalkthroughTransitionContent(Stream Content, string ContentType);

public interface IWalkthroughTransitionProcessor
{
    Task<WalkthroughTransitionRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed record WalkthroughTransitionRunResult(
    Guid TransitionId,
    WalkthroughTransitionStatus Status,
    int AttemptNumber,
    string? Error);
