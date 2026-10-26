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
        var attributedAi = ai.Where(item => item.PropertyReference != null);
        var attributedPropertyCount = await attributedAi
            .Select(item => item.PropertyReference)
            .Distinct()
            .LongCountAsync(cancellationToken);
        var attributedAiCost = await attributedAi
            .SumAsync(item => (decimal?)item.EstimatedCostUsd, cancellationToken) ?? 0;
        var averageAiCostPerProperty = attributedPropertyCount == 0
            ? 0
            : attributedAiCost / attributedPropertyCount;
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
            averageAiCostPerProperty,
            attributedPropertyCount,
            traffic,
            signIns,
            campaignOutcomes,
            jobFailures,
            aiUsage,
            audit);
    }

    public async Task<IReadOnlyList<AdminVideoChatRequestRow>> GetVideoChatRequestsAsync(
        AdminDateRange range,
        int maximumResults = 200,
        CancellationToken cancellationToken = default)
    {
        range.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 500);
        var requests = await dbContext.CampaignVideoChatMessages.AsNoTracking()
            .Where(item => item.Role == CampaignVideoChatRole.User
                && item.CreatedAtUtc >= range.FromUtc
                && item.CreatedAtUtc < range.ToUtc)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(maximumResults)
            .ToListAsync(cancellationToken);
        if (requests.Count == 0)
        {
            return [];
        }

        var deliverableIds = requests.Select(item => item.CampaignDeliverableId).Distinct().ToArray();
        var earliest = requests.Min(item => item.CreatedAtUtc);
        var answers = await dbContext.CampaignVideoChatMessages.AsNoTracking()
            .Where(item => item.Role == CampaignVideoChatRole.Assistant
                && deliverableIds.Contains(item.CampaignDeliverableId)
                && item.CreatedAtUtc >= earliest)
            .ToListAsync(cancellationToken);
        var answerIds = answers.Select(item => item.Id).ToArray();
        var revisions = await dbContext.CampaignVideoRevisions.AsNoTracking()
            .Where(item => answerIds.Contains(item.ChatMessageId))
            .ToListAsync(cancellationToken);
        var userIds = requests.Select(item => item.UserId).OfType<string>().Distinct().ToArray();
        var emails = await dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);
        var organizationIds = requests.Select(item => item.OrganizationId).Distinct().ToArray();
        var workspaces = await dbContext.Organizations.AsNoTracking()
            .Where(organization => organizationIds.Contains(organization.Id))
            .ToDictionaryAsync(organization => organization.Id, organization => organization.Name, cancellationToken);
        var propertyIds = requests.Select(item => item.PropertyId).Distinct().ToArray();
        var properties = await dbContext.Properties.AsNoTracking()
            .Where(property => propertyIds.Contains(property.Id))
            .ToDictionaryAsync(
                property => property.Id,
                property => $"{property.Address1}, {property.City}",
                cancellationToken);
        return requests.Select(request =>
        {
            // The assistant answers each request immediately, so its answer is the next message in that chat.
            var answer = answers
                .Where(item => item.CampaignDeliverableId == request.CampaignDeliverableId
                    && item.CreatedAtUtc >= request.CreatedAtUtc)
                .MinBy(item => item.CreatedAtUtc);
            var proposal = answer?.EditsJson is null
                ? null
                : Campaigns.CampaignVideoEditing.Deserialize(answer.EditsJson);
            var revision = answer is null
                ? null
                : revisions.Where(item => item.ChatMessageId == answer.Id).MaxBy(item => item.CreatedAtUtc);
            var (outcome, detail) = proposal is null
                ? ("No change proposed", null)
                : revision is null
                    ? ("Not rendered", null)
                    : revision.Status switch
                    {
                        CampaignVideoRevisionStatus.Completed => ("Rendered", $"Became version {revision.ResultVersion}"),
                        CampaignVideoRevisionStatus.Undone => ("Rendered, then undone", $"Was version {revision.ResultVersion}"),
                        CampaignVideoRevisionStatus.Failed => ("Render failed", revision.LastError),
                        _ => ("Rendering", null),
                    };
            return new AdminVideoChatRequestRow(
                request.CreatedAtUtc,
                request.UserId is not null && emails.TryGetValue(request.UserId, out var email) ? email : null,
                workspaces.GetValueOrDefault(request.OrganizationId),
                properties.GetValueOrDefault(request.PropertyId),
                Campaigns.CampaignVideoChatService.OutputName(request.Kind),
                request.VideoVersion,
                request.Text,
                answer?.Text,
                proposal?.Changes ?? [],
                outcome,
                detail,
                revision?.FixConfirmed,
                revision?.FixFeedback,
                revision?.FixConfirmedAtUtc);
        }).ToArray();
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
                item.Outcome,
                item.ActorReference))
            .ToListAsync(cancellationToken);
    }
}
