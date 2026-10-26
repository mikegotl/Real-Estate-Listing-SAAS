using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignGenerationProcessor
{
    public const int MaximumStageAttempts = 3;

    Task<CampaignStageRunResult?> ProcessNextStageAsync(CancellationToken cancellationToken = default);

    /// <summary>Renders the next video change a user asked for in a video chat, if any is waiting.</summary>
    Task<CampaignVideoRevisionRunResult?> ProcessNextRevisionAsync(CancellationToken cancellationToken = default);
}

public sealed record CampaignVideoRevisionRunResult(
    Guid RevisionId,
    bool Succeeded,
    bool WillRetry,
    int AttemptNumber,
    string? Error);

public sealed record CampaignStageRunResult(
    Guid JobId,
    CampaignGenerationStage Stage,
    bool Succeeded,
    bool StageCompleted,
    bool WillRetry,
    int AttemptNumber,
    string? Error);
