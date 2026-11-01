using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// The agent's view of one campaign's vertical video library: every video, its review, and its downloads. Every
/// call is scoped to the signed-in user's workspace, and every video is addressed by its deliverable id and version.
/// </summary>
public interface ICampaignVideoLibraryService
{
    /// <summary>The library for a campaign in the user's workspace; null when it has none (legacy campaigns).</summary>
    Task<CampaignVideoLibraryView?> GetAsync(string userId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the agent's decision on one exact rendered version. Another video's decisions are never touched, and
    /// a replacement render (a new version) has no decision until it is reviewed again.
    /// </summary>
    Task<CampaignVideoReviewState?> DecideAsync(
        string userId,
        Guid deliverableId,
        int expectedVersion,
        CampaignVideoReviewDecision decision,
        string? feedback,
        CancellationToken cancellationToken = default);

    /// <summary>One rendered campaign video by id. A version that is not current returns null, so a stale link never plays the wrong cut.</summary>
    Task<CampaignDownload?> OpenVideoAsync(
        string userId,
        Guid deliverableId,
        int? version,
        CancellationToken cancellationToken = default);

    /// <summary>The post caption for one library video as plain text.</summary>
    Task<CampaignDownload?> OpenCaptionAsync(string userId, Guid deliverableId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes every rendered library video, its caption and a manifest to a ZIP. Null when the campaign has
    /// no rendered library video in the user's workspace.
    /// </summary>
    Task<CampaignLibraryArchive?> OpenArchiveAsync(string userId, Guid jobId, CancellationToken cancellationToken = default);
}

public sealed record CampaignVideoLibraryView(
    Guid JobId,
    Guid LibraryId,
    CampaignVideoLibraryStatus Status,
    VerticalVideoSelection Selection,
    VerticalCampaignGoal? Goal,
    string? RuleVersion,
    IReadOnlyList<CampaignLibraryVideoView> Videos)
{
    public bool CanDownloadArchive => Videos.Any(video => video.IsRendered);
}

/// <summary>Whether the rendered file passed the automatic technical check (decode, size, length, streams).</summary>
public enum CampaignVideoTechnicalStatus
{
    Waiting,
    Passed,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record CampaignLibraryVideoView(
    Guid VariantId,
    Guid? DeliverableId,
    VerticalCreativeFormat Format,
    VerticalCampaignPurpose Purpose,
    int Sequence,
    int? Score,
    VerticalRecommendationStrength? Strength,
    IReadOnlyList<string> Reasons,
    string? Hook,
    string? CallToAction,
    int DurationSeconds,
    string RecipeVersion,
    CampaignVideoVariantStatus Status,
    CampaignVideoTechnicalStatus TechnicalStatus,
    string? Problem,
    int Version,
    CampaignVideoReviewState Review,
    VerticalVariantCaptions? Captions)
{
    public bool IsRendered => DeliverableId is not null && Status == CampaignVideoVariantStatus.Rendered;

    /// <summary>Each 9:16 library video can be posted to any of these.</summary>
    public static IReadOnlyList<string> IntendedPlatforms { get; } = ["TikTok", "Instagram Reels", "YouTube Shorts"];
}

/// <summary>The decision on the current version, and every earlier decision on this video, newest first.</summary>
public sealed record CampaignVideoReviewState(
    int Version,
    CampaignVideoReviewDecision? CurrentDecision,
    string? CurrentFeedback,
    IReadOnlyList<CampaignReviewEntry> History)
{
    public static CampaignVideoReviewState None(int version) => new(version, null, null, []);
}

public sealed record CampaignLibraryArchive(string FileName, Func<Stream, CancellationToken, Task> WriteAsync);

/// <summary>One entry of the library ZIP's manifest. No URLs, ids of other workspaces or secrets.</summary>
public sealed record CampaignLibraryManifestEntry(
    string Format,
    string Purpose,
    string? Hook,
    string? CallToAction,
    string FileName,
    string CaptionFileName,
    int Version,
    int DurationSeconds,
    string RecipeVersion,
    IReadOnlyList<string> IntendedPlatforms,
    string TechnicalValidation,
    string? EditorialDecision);

public sealed record CampaignLibraryManifest(
    string ManifestVersion,
    string RuleVersion,
    string Selection,
    string? Goal,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<CampaignLibraryManifestEntry> Videos)
{
    public const string CurrentVersion = "vertical-library-manifest-v1";
}
