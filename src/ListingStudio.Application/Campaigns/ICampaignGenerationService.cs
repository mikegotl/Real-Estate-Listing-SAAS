using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ICampaignGenerationService
{
    Task<CampaignGenerationResult?> EnqueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

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
}

public sealed record CampaignGenerationResult(
    Guid Id,
    Guid PropertyId,
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
    IReadOnlyList<CampaignDeliverableResult> Deliverables);

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
    DateTimeOffset? RenderedAtUtc);

public sealed record CampaignDownload(
    Stream Content,
    string ContentType,
    string FileName);
