using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignReviewService
{
    Task<IReadOnlyList<CampaignReviewItem>> ListAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampaignReviewItem>> ListSessionAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default);
    /// <summary>Legacy entry point: reviews the campaign's shared vertical video. Prefer <see cref="DecideDeliverableAsync"/>.</summary>
    Task DecideAsync(string userId, Guid jobId, int expectedVersion, CampaignVideoReviewDecision decision,
        string? feedback, CancellationToken cancellationToken = default);

    /// <summary>Reviews one exact rendered version of one video, identified by deliverable id.</summary>
    Task DecideDeliverableAsync(string userId, Guid deliverableId, int expectedVersion, CampaignVideoReviewDecision decision,
        string? feedback, CancellationToken cancellationToken = default);
}

public sealed record CampaignReviewItem(
    Guid JobId, Guid DeliverableId, Guid PropertyId, string Address, int Version,
    CampaignGenerationStatus CampaignStatus, CampaignGenerationStage Stage, string? Error,
    CampaignDeliverableStatus? DeliverableStatus, string Settings, DateTimeOffset CreatedAtUtc,
    CampaignVideoReviewDecision? Decision, IReadOnlyList<CampaignReviewEntry> History);

public sealed record CampaignReviewEntry(Guid Id, int Version, CampaignVideoReviewDecision Decision,
    string? Feedback, DateTimeOffset CreatedAtUtc);
