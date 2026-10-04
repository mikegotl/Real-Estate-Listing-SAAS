using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignGenerationProcessor
{
    public const int MaximumStageAttempts = 3;

    Task<CampaignStageRunResult?> ProcessNextStageAsync(CancellationToken cancellationToken = default);
}

public sealed record CampaignStageRunResult(
    Guid JobId,
    CampaignGenerationStage Stage,
    bool Succeeded,
    bool StageCompleted,
    bool WillRetry,
    int AttemptNumber,
    string? Error);
