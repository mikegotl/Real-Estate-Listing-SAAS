namespace ListingStudio.Application.Administration;

public static class PlatformAdministration
{
    public const string Role = "PlatformAdmin";
    public const string Policy = "PlatformAdministration";
}

public sealed record AdminDateRange(DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public static AdminDateRange LastDays(int days, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var to = timeProvider.GetUtcNow();
        return new AdminDateRange(to.AddDays(-Math.Min(days, 90)), to);
    }

    public void Validate()
    {
        if (FromUtc >= ToUtc || ToUtc - FromUtc > TimeSpan.FromDays(90))
        {
            throw new ArgumentOutOfRangeException(nameof(FromUtc), "The admin date range must be positive and no longer than 90 days.");
        }
    }
}

public sealed record AdminMetricRow(
    string Primary,
    string Secondary,
    long Count,
    long FailureCount = 0,
    decimal EstimatedCostUsd = 0,
    double AverageDurationMs = 0);

public sealed record AdminAuditRow(
    DateTimeOffset OccurredAtUtc,
    string Action,
    string ActorType,
    string TargetReference,
    string Outcome,
    string? ActorReference = null);

/// <summary>
/// One request a user made in a campaign video's chat, with the assistant's answer, the edits it proposed and what
/// became of them, kept for product review.
/// </summary>
public sealed record AdminVideoChatRequestRow(
    DateTimeOffset OccurredAtUtc,
    string? UserEmail,
    string? WorkspaceName,
    string? PropertyAddress,
    string Video,
    int VideoVersion,
    string Request,
    string? AssistantReply,
    IReadOnlyList<string> ProposedChanges,
    string Outcome,
    string? OutcomeDetail);

public sealed record PlatformAdminDashboard(
    AdminDateRange Range,
    DateTimeOffset? MetricsResetAtUtc,
    long RequestCount,
    long SignInCount,
    long ActiveUserCount,
    long CampaignCount,
    long FailedCampaignCount,
    long AiCallCount,
    long FailedAiCallCount,
    decimal EstimatedAiCostUsd,
    decimal AverageAiCostPerPropertyUsd,
    long AiAttributedPropertyCount,
    IReadOnlyList<AdminMetricRow> Traffic,
    IReadOnlyList<AdminMetricRow> SignIns,
    IReadOnlyList<AdminMetricRow> CampaignOutcomes,
    IReadOnlyList<AdminMetricRow> BackgroundJobFailures,
    IReadOnlyList<AdminMetricRow> AiUsage,
    IReadOnlyList<AdminAuditRow> RecentAuditEvents);

public interface IPlatformAdminDashboardService
{
    Task<PlatformAdminDashboard> GetAsync(
        AdminDateRange range,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminAuditRow>> GetAuditEventsAsync(
        AdminDateRange range,
        int maximumResults = 200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminVideoChatRequestRow>> GetVideoChatRequestsAsync(
        AdminDateRange range,
        int maximumResults = 200,
        CancellationToken cancellationToken = default);
}

public interface IPlatformAdminMetricsResetService
{
    Task<DateTimeOffset> ResetAsync(
        string actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed record AiCallObservation(
    string Provider,
    string Model,
    string Operation,
    bool Succeeded,
    TimeSpan Duration,
    decimal EstimatedCostUsd,
    string? FailureCategory = null);

public interface IAiUsageRecorder
{
    ValueTask RecordAsync(AiCallObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries the property responsible for an outbound AI/API call across the asynchronous provider request. The
/// dashboard uses only aggregate counts and costs; it never displays the property identifier.
/// </summary>
public interface IAiUsagePropertyContext
{
    Guid? PropertyId { get; }

    IDisposable BeginProperty(Guid propertyId);
}

public sealed record OperationalObservation(
    string Kind,
    string Category,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

public interface IOperationalEventRecorder
{
    ValueTask RecordAsync(OperationalObservation observation, CancellationToken cancellationToken = default);

    ValueTask RecordSuccessfulSignInAsync(
        string userId,
        string method,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);
}
