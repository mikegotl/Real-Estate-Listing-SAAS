using ListingStudio.Application.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminDashboardService(ApplicationDbContext dbContext)
    : IPlatformAdminDashboardService
{
    public async Task<PlatformAdminDashboard> GetAsync(
        AdminDateRange range,
        CancellationToken cancellationToken = default)
    {
        range.Validate();
        var resetAtUtc = await dbContext.PlatformMetricsResets.AsNoTracking()
            .OrderByDescending(item => item.ResetAtUtc)
            .Select(item => (DateTimeOffset?)item.ResetAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var metricsFromUtc = resetAtUtc is not null && resetAtUtc > range.FromUtc
            ? resetAtUtc.Value
            : range.FromUtc;
        var operational = dbContext.OperationalEvents.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= metricsFromUtc && item.OccurredAtUtc < range.ToUtc);
        var ai = dbContext.AiUsageEvents.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= metricsFromUtc && item.OccurredAtUtc < range.ToUtc);
        var campaigns = dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(item => item.CreatedAtUtc >= metricsFromUtc && item.CreatedAtUtc < range.ToUtc);

        var trafficGroups = await operational.Where(item => item.Kind == "request")
            .GroupBy(item => new { item.Category, item.Outcome })
            .Select(group => new { group.Key.Category, group.Key.Outcome, Count = group.LongCount() })
            .OrderByDescending(item => item.Count)
            .Take(20)
            .ToListAsync(cancellationToken);
        var traffic = trafficGroups
            .Select(item => new AdminMetricRow(item.Category, item.Outcome, item.Count))
            .ToList();
        var signInGroups = await operational.Where(item => item.Kind == "sign-in")
            .GroupBy(item => new { item.Category, item.Outcome })
            .Select(group => new { group.Key.Category, group.Key.Outcome, Count = group.LongCount() })
            .OrderByDescending(item => item.Count)
            .ToListAsync(cancellationToken);
        var signIns = signInGroups
            .Select(item => new AdminMetricRow(item.Category, item.Outcome, item.Count))
            .ToList();
        var campaignOutcomeGroups = await campaigns
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() })
            .OrderByDescending(item => item.Count)
            .ToListAsync(cancellationToken);
        var campaignOutcomes = campaignOutcomeGroups
            .Select(item => new AdminMetricRow(item.Status.ToString(), "campaign", item.Count))
            .ToList();
        var jobFailureGroups = await campaigns
            .Where(item => item.Status == CampaignGenerationStatus.Failed)
            .GroupBy(item => item.CurrentStage)
            .Select(group => new { Stage = group.Key, Count = group.LongCount() })
            .OrderByDescending(item => item.Count)
            .ToListAsync(cancellationToken);
        var jobFailures = jobFailureGroups
            .Select(item => new AdminMetricRow(item.Stage.ToString(), "failed", item.Count, item.Count))
            .ToList();
        var aiUsageGroups = await ai
            .GroupBy(item => new { item.Provider, item.Model, item.Operation })
            .Select(group => new
            {
                group.Key.Provider,
                group.Key.Model,
                group.Key.Operation,
                Count = group.LongCount(),
                FailureCount = group.LongCount(item => !item.Succeeded),
                EstimatedCostUsd = group.Sum(item => item.EstimatedCostUsd),
                AverageDurationMs = group.Average(item => (double)item.DurationMs),
            })
            .OrderByDescending(item => item.EstimatedCostUsd)
            .ThenByDescending(item => item.Count)
            .ToListAsync(cancellationToken);
        var aiUsage = aiUsageGroups
            .Select(item => new AdminMetricRow(
                item.Provider,
                item.Model + " · " + item.Operation,
                item.Count,
                item.FailureCount,
                item.EstimatedCostUsd,
                item.AverageDurationMs))
            .ToList();

        var requestCount = await operational.LongCountAsync(item => item.Kind == "request", cancellationToken);
        var signInCount = await operational.LongCountAsync(
            item => item.Kind == "sign-in" && item.Outcome == "succeeded",
            cancellationToken);
        var activeUsers = await dbContext.Users.AsNoTracking().LongCountAsync(
            item => item.LastSuccessfulSignInAtUtc >= metricsFromUtc
                && item.LastSuccessfulSignInAtUtc < range.ToUtc,
            cancellationToken);
        var campaignCount = await campaigns.LongCountAsync(cancellationToken);
        var failedCampaignCount = await campaigns.LongCountAsync(
            item => item.Status == CampaignGenerationStatus.Failed,
            cancellationToken);
        var aiCallCount = await ai.LongCountAsync(cancellationToken);
        var failedAiCallCount = await ai.LongCountAsync(item => !item.Succeeded, cancellationToken);
        var aiCost = await ai.SumAsync(item => (decimal?)item.EstimatedCostUsd, cancellationToken) ?? 0;
        var audit = await GetAuditEventsAsync(range, 25, cancellationToken);

        return new PlatformAdminDashboard(
            range,
            resetAtUtc,
            requestCount,
            signInCount,
            activeUsers,
            campaignCount,
            failedCampaignCount,
            aiCallCount,
            failedAiCallCount,
            aiCost,
            traffic,
            signIns,
            campaignOutcomes,
            jobFailures,
            aiUsage,
            audit);
    }

    public async Task<IReadOnlyList<AdminAuditRow>> GetAuditEventsAsync(
        AdminDateRange range,
        int maximumResults = 200,
        CancellationToken cancellationToken = default)
    {
        range.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 500);
        return await dbContext.PlatformAdminAuditEvents.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= range.FromUtc && item.OccurredAtUtc < range.ToUtc)
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(maximumResults)
            .Select(item => new AdminAuditRow(
                item.OccurredAtUtc,
                item.Action,
                item.ActorType,
                item.TargetReference,
                item.Outcome))
            .ToListAsync(cancellationToken);
    }
}
