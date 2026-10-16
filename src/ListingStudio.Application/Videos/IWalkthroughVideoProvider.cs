using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Videos;

/// <summary>
/// Generates a walk-through clip that starts on one listing photo and ends on the next. Generation is asynchronous:
/// a submission returns a provider request ID that is polled until the clip is ready.
/// </summary>
public interface IWalkthroughVideoProvider
{
    bool IsEnabled { get; }

    /// <summary>Identifies the provider, model, and prompt; a change produces new clips instead of reusing old ones.</summary>
    string GenerationVersion { get; }

    /// <summary>The clip length requested from the provider.</summary>
    int ClipDurationMs { get; }

    Task<string> SubmitAsync(
        WalkthroughVideoSubmission submission,
        CancellationToken cancellationToken = default);

    Task<WalkthroughVideoPollResult> PollAsync(
        string providerRequestId,
        CancellationToken cancellationToken = default);
}

public sealed record WalkthroughVideoFrame(Stream Content, string ContentType);

/// <summary>
/// One clip to generate. <see cref="Direction"/> is the way the buyer turns or climbs to reach the end frame's space,
/// as the agent set it on the walkthrough map; null when the two shots are in the same space or no direction is set.
/// </summary>
public sealed record WalkthroughVideoSubmission(
    WalkthroughVideoFrame StartFrame,
    WalkthroughVideoFrame EndFrame,
    string IdempotencyKey,
    WalkthroughDirection? Direction = null);

public enum WalkthroughVideoState
{
    InProgress,
    Completed,
    Failed,
}

/// <summary>A poll result. <see cref="Content"/> holds the finished MP4 when the state is completed.</summary>
public sealed record WalkthroughVideoPollResult(
    WalkthroughVideoState State,
    Stream? Content,
    string Provider,
    string Model,
    string? FailureReason);
