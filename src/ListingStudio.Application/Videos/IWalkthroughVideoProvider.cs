using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Videos;

public static class FullTourVideoPolicy
{
    public const int MinimumFrames = 2;
    public const int MaximumFrames = 10;
}

public static class NativePresenterVideoPolicy
{
    // The same portrait is the first and last frame, leaving eight of Kling 4's ten slots for the property.
    public const int MinimumTourFrames = 1;
    public const int MaximumTourFrames = 8;

    /// <summary>Kling 4 makes 3 to 30 seconds per call; a library video longer than that keeps the established edit.</summary>
    public const int MinimumDurationSeconds = 3;
    public const int MaximumDurationSeconds = 30;
    public const int DefaultDurationSeconds = 30;

    /// <summary>Fewer keyframes for a short video, so each space is on screen long enough to read.</summary>
    public static int MaximumTourFramesFor(int durationSeconds) =>
        Math.Clamp(durationSeconds * MaximumTourFrames / DefaultDurationSeconds, MinimumTourFrames, MaximumTourFrames);

    /// <summary>
    /// While Kling4NativePresenterV1 is on for pilot workspaces only, every library video is a shorter, cheaper
    /// 15-second 720p Kling video. Once the switch is on for every workspace (live), each video keeps its recipe
    /// length and Kling makes it at the configured 1080p.
    /// </summary>
    public const int PilotDurationSeconds = 15;

    public const string PilotResolution = "720p";

    /// <summary>The library video's length, given its recipe length and whether the presenter is still in pilot.</summary>
    public static int DurationFor(int recipeSeconds, bool pilot) => pilot ? PilotDurationSeconds : recipeSeconds;

    /// <summary>The Kling resolution; null keeps the configured resolution (1080p).</summary>
    public static string? ResolutionFor(bool pilot) => pilot ? PilotResolution : null;
}

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

    /// <summary>Identifies the Kling 4 ordered-keyframe full-tour model and prompt.</summary>
    string FullTourGenerationVersion { get; }

    /// <summary>The requested full-tour duration.</summary>
    int FullTourDurationMs { get; }

    /// <summary>Identifies the Kling 4 native-presenter prompt and ordered-keyframe request.</summary>
    string NativePresenterGenerationVersion { get; }

    /// <summary>The requested native-presenter duration.</summary>
    int NativePresenterDurationMs { get; }

    Task<string> SubmitAsync(
        WalkthroughVideoSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>Identifies the model and prompt for drone intros; a change marks existing intros out of date.</summary>
    string AerialIntroGenerationVersion { get; }

    /// <summary>
    /// Asks for a rising pull-back that starts on the Exterior front photo and climbs to the requested height. The
    /// finished clip is polled like a walk-through and played backwards so it lands on the photo.
    /// </summary>
    Task<string> SubmitAerialIntroAsync(
        AerialIntroVideoSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates one continuous tour from two to ten approved Tour Map photos. Frames remain in the user's order and
    /// each direction describes the move from that frame to the next.
    /// </summary>
    Task<string> SubmitFullTourAsync(
        FullTourVideoSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>Builds the exact prompt that <see cref="SubmitFullTourAsync"/> sends to the provider.</summary>
    string BuildFullTourPrompt(FullTourVideoSubmission submission);

    /// <summary>
    /// Generates one native-audio campaign video. The portrait opens and closes the video while approved Tour Map
    /// photos occupy the ordered middle keyframes.
    /// </summary>
    Task<NativePresenterSubmissionResult> SubmitNativePresenterAsync(
        NativePresenterVideoSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>Estimated provider charge for one native-presenter video, shown before an admin spends it.</summary>
    decimal EstimatedNativePresenterCostUsd => 0m;

    /// <summary>Builds the exact prompt that <see cref="SubmitNativePresenterAsync"/> sends to the provider.</summary>
    string BuildNativePresenterPrompt(NativePresenterVideoSubmission submission);

    /// <summary>
    /// Builds the exact HTTP request <see cref="SubmitNativePresenterAsync"/> sends, given the image URLs the photos
    /// were uploaded to. Credentials are sent as headers and are never part of it.
    /// </summary>
    ProviderRequestPreview BuildNativePresenterRequest(
        NativePresenterVideoSubmission submission,
        string agentImageUrl,
        IReadOnlyList<string> tourImageUrls);

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

/// <summary>One drone intro to generate from the Exterior front photo, climbing to <see cref="HeightFeet"/>.</summary>
public sealed record AerialIntroVideoSubmission(
    WalkthroughVideoFrame Frame,
    int HeightFeet,
    string IdempotencyKey);

public sealed record FullTourVideoFrame(
    WalkthroughVideoFrame Frame,
    string StopName,
    WalkthroughDirection? DirectionToNext = null);

public sealed record FullTourVideoSubmission(
    IReadOnlyList<FullTourVideoFrame> Frames,
    string IdempotencyKey);

public sealed record NativePresenterTourFrame(
    WalkthroughVideoFrame Frame,
    string StopName,
    string? ApprovedHighlight = null,
    WalkthroughDirection? DirectionToNext = null);

public sealed record NativePresenterVideoSubmission(
    WalkthroughVideoFrame AgentPortrait,
    IReadOnlyList<NativePresenterTourFrame> TourFrames,
    string OpeningLine,
    string ClosingLine,
    string CampaignGoal,
    string MusicDirection,
    string IdempotencyKey,
    string? TourVoiceover = null,
    int DurationSeconds = NativePresenterVideoPolicy.DefaultDurationSeconds,
    string? Resolution = null);

/// <summary>One provider HTTP request: method, endpoint and the JSON body, without credentials.</summary>
public sealed record ProviderRequestPreview(string Method, string Url, string BodyJson);

/// <summary>The provider's request ID and the exact request that was sent to get it.</summary>
public sealed record NativePresenterSubmissionResult(string RequestId, ProviderRequestPreview Request);

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
