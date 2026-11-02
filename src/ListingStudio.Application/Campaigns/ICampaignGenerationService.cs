using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignGenerationService
{
    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CancellationToken cancellationToken = default);

    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CancellationToken cancellationToken = default);

    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as the other overloads, but asks for several recommended vertical videos instead of one shared vertical
    /// edit. The request only takes effect while VerticalVideoLibraryV1 is on for the workspace; otherwise the
    /// campaign is the legacy one, exactly as before.
    /// </summary>
    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        VerticalVideoSelection verticalSelection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for vertical library videos with the agent's goal and, for <see cref="VerticalVideoSelection.AgentChosen"/>,
    /// the formats the agent picked. Agent-chosen formats must be eligible when the request is made. With the switch
    /// off the request is the legacy campaign, exactly as before.
    /// </summary>
    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        VerticalLibraryRequest verticalRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues the selected visual strategy. Kling4FullTour only takes effect while its release switch is on for the
    /// workspace; otherwise the established strategy is used without changing the established campaign path.
    /// </summary>
    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        CampaignVideoStrategy videoStrategy,
        VerticalLibraryRequest? verticalRequest = null,
        CancellationToken cancellationToken = default);

    /// <summary>Whether VerticalVideoLibraryV1 is on for the signed-in user's workspace (kill switch included).</summary>
    Task<bool> IsVerticalLibraryEnabledAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Whether the Kling 4 full-tour pilot is on for the signed-in user's workspace.</summary>
    Task<bool> IsKlingFullTourEnabledAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Whether the single-call Kling 4 native presenter pilot is on for the signed-in user's workspace.</summary>
    Task<bool> IsKlingNativePresenterEnabledAsync(string userId, CancellationToken cancellationToken = default);

    Task<CampaignGenerationResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<CampaignGenerationResult?> GetAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(
        string userId,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<CampaignDownload?> OpenDeliverableAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default);

    Task<CampaignDownload?> OpenStillPostAsync(
        string userId,
        Guid jobId,
        CampaignStillPostKind kind,
        CancellationToken cancellationToken = default);
}

public sealed record CampaignGenerationResult(
    Guid Id,
    Guid PropertyId,
    CampaignOutputSelection RequestedOutputs,
    CampaignCreativeOptions CreativeOptions,
    CampaignNarrationPreferences NarrationPreferences,
    CampaignGenerationStatus Status,
    CampaignGenerationStage CurrentStage,
    int ProgressPercent,
    string? LastError,
    bool CancellationRequested,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? SocialCaptionLong,
    string? SocialCaptionShort,
    IReadOnlyList<CampaignStageResult> Stages,
    IReadOnlyList<CampaignDeliverableResult> Deliverables,
    IReadOnlyList<CampaignStillPostResult> StillPosts,
    CampaignVerticalRequestResult? VerticalRequest = null,
    CampaignVideoStrategy VideoStrategy = CampaignVideoStrategy.Established);

/// <summary>A campaign's vertical library request: how the videos were chosen, the goal, and any agent-picked formats.</summary>
public sealed record CampaignVerticalRequestResult(
    VerticalVideoSelection Selection,
    VerticalCampaignGoal? Goal,
    IReadOnlyList<VerticalCreativeFormat> Formats);

/// <summary>What the agent asked for in the recommendation-led vertical workflow.</summary>
public sealed record VerticalLibraryRequest(
    VerticalVideoSelection Selection,
    VerticalCampaignGoal? Goal = null,
    IReadOnlyList<VerticalCreativeFormat>? Formats = null)
{
    /// <summary>Checks the request shape; eligibility is checked against the listing when it is queued.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Selection))
        {
            throw new ArgumentOutOfRangeException(nameof(Selection));
        }

        if (Goal is { } goal && !Enum.IsDefined(goal))
        {
            throw new ArgumentOutOfRangeException(nameof(Goal));
        }

        if (Selection == VerticalVideoSelection.AgentChosen)
        {
            CampaignGenerationJob.FormatVerticalFormats(Formats);
        }
        else if (Formats is { Count: > 0 })
        {
            throw new ArgumentException("Pick formats only with an agent-chosen selection.", nameof(Formats));
        }
    }
}

public sealed record CampaignStageResult(
    CampaignGenerationStage Stage,
    CampaignStageStatus Status,
    int AttemptCount);

public enum CampaignStageStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record CampaignDeliverableResult(
    CampaignOutputKind Kind,
    CampaignDeliverableStatus Status,
    int DurationSeconds,
    string AspectRatio,
    long? FileSize,
    DateTimeOffset? RenderedAtUtc,
    int Version = 1);

public sealed record CampaignStillPostResult(
    CampaignStillPostKind Kind,
    CampaignStillPostStatus Status,
    int Width,
    int Height,
    string Caption,
    long? FileSize,
    DateTimeOffset? RenderedAtUtc);

public sealed record CampaignDownload(
    Stream Content,
    string ContentType,
    string FileName);
