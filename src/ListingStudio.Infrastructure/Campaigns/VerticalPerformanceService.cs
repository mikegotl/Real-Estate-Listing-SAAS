using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Manual and CSV performance tracking for vertical library videos, and the admin rule evaluation. Every query is
/// scoped to the caller's workspace and every call needs VerticalVideoLibraryV1 on for it. Logs carry counts and ids
/// only, never post URLs, numbers or listing content.
/// </summary>
public sealed partial class VerticalPerformanceService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> users,
    IFeatureStateService featureStateService,
    TimeProvider timeProvider,
    ILogger<VerticalPerformanceService> logger) : IVerticalPerformanceService, IVerticalRuleEvaluationService
{
    public async Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await AuthorizeAsync(userId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PerformanceTrackingDisabledException)
        {
            return false;
        }
    }

    public async Task<VerticalPerformanceView?> GetCampaignAsync(string userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var library = await dbContext.CampaignVideoLibraries.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.CampaignGenerationJobId == jobId
                && candidate.Selection != VerticalVideoSelection.LegacySingle)
            .Select(candidate => new { candidate.PropertyId, candidate.CampaignGenerationJobId })
            .SingleOrDefaultAsync(cancellationToken);
        if (library is null)
        {
            return null;
        }

        var groups = await LoadGroupsAsync(organizationId, [jobId], cancellationToken);
        return new VerticalPerformanceView("This campaign", library.PropertyId, jobId, null, VerticalPerformanceCalculator.CalculationVersion,
            groups.GetValueOrDefault(jobId) is { } videos ? VerticalPerformanceCalculator.Compare(videos) : []);
    }

    public async Task<Guid> AddDistributionAsync(string userId, Guid deliverableId, DistributionPlatform platform, string postUrl,
        DateTimeOffset publishedAtUtc, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var variant = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.Deliverable != null && candidate.Deliverable.Id == deliverableId
                && candidate.Library.Selection != VerticalVideoSelection.LegacySingle)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Library.PropertyId,
                candidate.Library.CampaignGenerationJobId,
                Rendered = candidate.Deliverable!.Status == CampaignDeliverableStatus.Rendered,
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Only videos from a vertical video library in your workspace can be tracked.");
        if (!variant.Rendered)
        {
            throw new InvalidOperationException("This video hasn't finished rendering yet.");
        }

        var record = CampaignDistributionRecord.Create(organizationId, variant.PropertyId, variant.CampaignGenerationJobId, variant.Id,
            deliverableId, platform, postUrl, publishedAtUtc, userId, timeProvider.GetUtcNow());
        if (await dbContext.CampaignDistributionRecords.AnyAsync(candidate => candidate.OrganizationId == organizationId
            && candidate.PostUrl == record.PostUrl, cancellationToken))
        {
            throw new InvalidOperationException("That post is already recorded in this workspace.");
        }

        dbContext.CampaignDistributionRecords.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        LogDistributionAdded(record.Id, platform);
        return record.Id;
    }

    public async Task AddSnapshotAsync(string userId, Guid distributionId, CampaignMetricValues values, DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var record = await dbContext.CampaignDistributionRecords.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == distributionId && candidate.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("This post is unavailable in your workspace.");
        var snapshot = CampaignMetricSnapshot.Create(record, values, capturedAtUtc, CampaignMetricSource.Manual, userId, timeProvider.GetUtcNow());
        if (await dbContext.CampaignMetricSnapshots.AnyAsync(candidate => candidate.DistributionRecordId == record.Id
            && candidate.CapturedAtUtc == snapshot.CapturedAtUtc, cancellationToken))
        {
            throw new InvalidOperationException("Numbers for this post at that time are already recorded.");
        }

        dbContext.CampaignMetricSnapshots.Add(snapshot);
        await dbContext.SaveChangesAsync(cancellationToken);
        LogSnapshotsAdded(1, CampaignMetricSource.Manual);
    }

    public async Task<MetricImportResult> ImportCsvAsync(string userId, Stream csv, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        using var reader = new StreamReader(csv);
        var (rows, errors) = MetricCsvParser.Parse(reader);
        var problems = errors.ToList();
        if (rows.Count == 0 && problems.Count == 0)
        {
            problems.Add("The file has no rows.");
        }

        var urls = rows.Select(row => row.PostUrl).Distinct(StringComparer.Ordinal).ToArray();
        var records = await dbContext.CampaignDistributionRecords.AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && urls.Contains(record.PostUrl))
            .ToDictionaryAsync(record => record.PostUrl, StringComparer.Ordinal, cancellationToken);
        var recordIds = records.Values.Select(record => record.Id).ToArray();
        var existing = (await dbContext.CampaignMetricSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.OrganizationId == organizationId && recordIds.Contains(snapshot.DistributionRecordId))
                .Select(snapshot => new { snapshot.DistributionRecordId, snapshot.CapturedAtUtc })
                .ToListAsync(cancellationToken))
            .Select(item => (item.DistributionRecordId, item.CapturedAtUtc.UtcTicks)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        var snapshots = new List<CampaignMetricSnapshot>();
        foreach (var row in rows)
        {
            if (!records.TryGetValue(row.PostUrl, out var record))
            {
                problems.Add($"Line {row.Line}: that post isn't recorded yet. Add it to its video first.");
                continue;
            }

            if (record.Platform != row.Platform)
            {
                problems.Add($"Line {row.Line}: that post was recorded as {record.Platform}, not {row.Platform}.");
                continue;
            }

            try
            {
                var snapshot = CampaignMetricSnapshot.Create(record, row.Values, row.CapturedAtUtc, CampaignMetricSource.CsvImport, userId, now);
                if (existing.Contains((record.Id, snapshot.CapturedAtUtc.UtcTicks)))
                {
                    problems.Add($"Line {row.Line}: numbers for this post at that time are already recorded.");
                    continue;
                }

                snapshots.Add(snapshot);
            }
            catch (ArgumentException exception)
            {
                problems.Add($"Line {row.Line}: {exception.Message.Split(" (Parameter", 2)[0]}");
            }
        }

        if (problems.Count > 0)
        {
            LogImportRejected(problems.Count);
            return new MetricImportResult(0, problems);
        }

        dbContext.CampaignMetricSnapshots.AddRange(snapshots);
        await dbContext.SaveChangesAsync(cancellationToken);
        LogSnapshotsAdded(snapshots.Count, CampaignMetricSource.CsvImport);
        return new MetricImportResult(snapshots.Count, []);
    }

    public async Task<IReadOnlyList<VerticalPerformanceView>> GetSuitePerformanceAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAdminAsync(userId, cancellationToken);
        var cases = await dbContext.VerticalCreativeTestCases.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId && item.CampaignGenerationJobId != null)
            .Select(item => new { item.SuiteId, item.Suite.PropertyId, item.Suite.CreatedAtUtc, JobId = item.CampaignGenerationJobId!.Value })
            .ToListAsync(cancellationToken);
        var groups = await LoadGroupsAsync(organizationId, cases.Select(item => item.JobId).Distinct().ToArray(), cancellationToken);
        return cases.GroupBy(item => (item.SuiteId, item.PropertyId, item.CreatedAtUtc))
            .OrderByDescending(group => group.Key.CreatedAtUtc)
            .Select(group =>
            {
                // A creative test compares its own videos: same property, same suite.
                var videos = group.Select(item => item.JobId).Distinct().SelectMany(jobId => groups.GetValueOrDefault(jobId) ?? []).ToArray();
                return new VerticalPerformanceView($"Creative test started {group.Key.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC", group.Key.PropertyId,
                    null, group.Key.SuiteId, VerticalPerformanceCalculator.CalculationVersion, VerticalPerformanceCalculator.Compare(videos));
            })
            .Where(view => view.HasMetrics)
            .ToArray();
    }

    public async Task<VerticalRuleEvaluationView> CreateDraftAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAdminAsync(userId, cancellationToken);
        var inputs = await LoadEvaluationInputsAsync(organizationId, cancellationToken);
        var (segments, meets) = VerticalPerformanceCalculator.Evaluate(inputs);
        var report = VerticalRuleEvaluationReport.CreateDraft(organizationId, userId, await ActiveRuleVersionAsync(organizationId, cancellationToken),
            VerticalPerformanceCalculator.CalculationVersion, inputs.Count, inputs.Sum(input => input.Totals.Views), meets,
            VerticalPerformanceCalculator.ToJson(segments), timeProvider.GetUtcNow());
        dbContext.VerticalRuleEvaluationReports.Add(report);
        await dbContext.SaveChangesAsync(cancellationToken);
        LogDraftCreated(report.Id, inputs.Count, meets);
        return ToView(report);
    }

    public async Task<IReadOnlyList<VerticalRuleEvaluationView>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAdminAsync(userId, cancellationToken);
        var reports = await dbContext.VerticalRuleEvaluationReports.AsNoTracking()
            .Where(report => report.OrganizationId == organizationId)
            .OrderByDescending(report => report.CreatedAtUtc).Take(20)
            .ToListAsync(cancellationToken);
        return reports.Select(ToView).ToArray();
    }

    public async Task PromoteAsync(string userId, Guid reportId, string ruleVersion, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAdminAsync(userId, cancellationToken);
        if (!VerticalPerformanceCalculator.ImplementedRuleVersions.Contains(ruleVersion ?? string.Empty))
        {
            throw new InvalidOperationException("That rule version isn't implemented, so it can't be promoted.");
        }

        var report = await dbContext.VerticalRuleEvaluationReports
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId && candidate.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("This report is unavailable in your workspace.");
        report.Promote(ruleVersion!, userId, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        LogPromoted(report.Id, ruleVersion!);
    }

    public async Task<string> GetActiveRuleVersionAsync(string userId, CancellationToken cancellationToken = default) =>
        await ActiveRuleVersionAsync(await AuthorizeAdminAsync(userId, cancellationToken), cancellationToken);

    private async Task<string> ActiveRuleVersionAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await dbContext.VerticalRuleEvaluationReports.AsNoTracking()
            .Where(report => report.OrganizationId == organizationId && report.Status == VerticalRuleReportStatus.Promoted)
            .OrderByDescending(report => report.PromotedAtUtc)
            .Select(report => report.PromotedRuleVersion)
            .FirstOrDefaultAsync(cancellationToken)
        ?? VerticalFormatRules.Version;

    /// <summary>The library videos of the given campaigns with their posts and latest snapshot per post, keyed by campaign.</summary>
    private async Task<Dictionary<Guid, List<(Guid VariantId, Guid DeliverableId, VerticalCreativeFormat Format, string HookVariant,
        VerticalCampaignPurpose Purpose, IReadOnlyList<VerticalPerformancePost> Posts)>>> LoadGroupsAsync(
        Guid organizationId, Guid[] jobIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<(Guid, Guid, VerticalCreativeFormat, string, VerticalCampaignPurpose, IReadOnlyList<VerticalPerformancePost>)>>();
        if (jobIds.Length == 0)
        {
            return result;
        }

        var variants = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(variant => variant.OrganizationId == organizationId && jobIds.Contains(variant.Library.CampaignGenerationJobId)
                && variant.Library.Selection != VerticalVideoSelection.LegacySingle
                && variant.Deliverable != null && variant.Deliverable.Status == CampaignDeliverableStatus.Rendered)
            .OrderBy(variant => variant.Sequence)
            .Select(variant => new
            {
                variant.Id,
                DeliverableId = variant.Deliverable!.Id,
                variant.Format,
                variant.HookVariant,
                variant.Purpose,
                variant.Library.CampaignGenerationJobId,
            })
            .ToListAsync(cancellationToken);
        var variantIds = variants.Select(variant => variant.Id).ToArray();
        var posts = await LoadPostsAsync(organizationId, variantIds, cancellationToken);
        foreach (var variant in variants)
        {
            if (!result.TryGetValue(variant.CampaignGenerationJobId, out var list))
            {
                result[variant.CampaignGenerationJobId] = list = [];
            }

            list.Add((variant.Id, variant.DeliverableId, variant.Format, variant.HookVariant, variant.Purpose,
                posts.GetValueOrDefault(variant.Id) ?? []));
        }

        return result;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<VerticalPerformancePost>>> LoadPostsAsync(Guid organizationId, Guid[] variantIds,
        CancellationToken cancellationToken)
    {
        var records = await dbContext.CampaignDistributionRecords.AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && variantIds.Contains(record.CampaignVideoVariantId))
            .OrderBy(record => record.PublishedAtUtc)
            .ToListAsync(cancellationToken);
        var recordIds = records.Select(record => record.Id).ToArray();
        var snapshots = await dbContext.CampaignMetricSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.OrganizationId == organizationId && recordIds.Contains(snapshot.DistributionRecordId))
            .ToListAsync(cancellationToken);
        var byRecord = snapshots.GroupBy(snapshot => snapshot.DistributionRecordId).ToDictionary(group => group.Key, group => group.ToArray());
        return records.GroupBy(record => record.CampaignVideoVariantId).ToDictionary(group => group.Key, group =>
            (IReadOnlyList<VerticalPerformancePost>)group.Select(record =>
            {
                var readings = byRecord.GetValueOrDefault(record.Id) ?? [];
                var latest = readings.OrderByDescending(snapshot => snapshot.CapturedAtUtc).FirstOrDefault();
                return new VerticalPerformancePost(record.Id, record.Platform, record.PostUrl, record.PublishedAtUtc, readings.Length,
                    latest?.CapturedAtUtc, latest?.Values);
            }).ToArray());
    }

    private async Task<IReadOnlyList<VerticalRuleEvaluationInput>> LoadEvaluationInputsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        // Render regression campaigns test the renderer, not a format choice, so they never feed the rule evaluation.
        var regressionJobs = await dbContext.TikTokTestSuites.AsNoTracking()
            .Where(suite => suite.OrganizationId == organizationId)
            .Select(suite => new { suite.ShortVoiceoverJobId, suite.MusicOnlyJobId, suite.FullVoiceoverJobId })
            .ToListAsync(cancellationToken);
        var excluded = regressionJobs.SelectMany(suite => new[] { suite.ShortVoiceoverJobId, suite.MusicOnlyJobId, suite.FullVoiceoverJobId })
            .OfType<Guid>().ToHashSet();
        var measuredVariantIds = await dbContext.CampaignDistributionRecords.AsNoTracking()
            .Where(record => record.OrganizationId == organizationId
                && dbContext.CampaignMetricSnapshots.Any(snapshot => snapshot.DistributionRecordId == record.Id))
            .Select(record => record.CampaignVideoVariantId).Distinct()
            .ToListAsync(cancellationToken);
        var variants = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(variant => variant.OrganizationId == organizationId && measuredVariantIds.Contains(variant.Id)
                && variant.Library.Selection != VerticalVideoSelection.LegacySingle)
            .Select(variant => new
            {
                variant.Id,
                variant.Format,
                variant.Library.PropertyId,
                variant.Library.CampaignGenerationJobId,
                variant.Library.RecommendationSnapshotJson,
            })
            .ToListAsync(cancellationToken);
        variants = variants.Where(variant => !excluded.Contains(variant.CampaignGenerationJobId)).ToList();
        var propertyIds = variants.Select(variant => variant.PropertyId).Distinct().ToArray();
        var jobIds = variants.Select(variant => variant.CampaignGenerationJobId).Distinct().ToArray();
        var properties = await dbContext.Properties.AsNoTracking()
            .Where(property => property.OrganizationId == organizationId && propertyIds.Contains(property.Id))
            .Select(property => new { property.Id, property.PropertyType, property.ListingPrice })
            .ToDictionaryAsync(property => property.Id, cancellationToken);
        var photoCounts = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && propertyIds.Contains(media.PropertyId) && !media.IsInGallery)
            .GroupBy(media => media.PropertyId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        var withVideo = (await dbContext.PropertyVideos.AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && propertyIds.Contains(video.PropertyId))
            .Select(video => video.PropertyId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var requesters = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.OrganizationId == organizationId && jobIds.Contains(job.Id))
            .ToDictionaryAsync(job => job.Id, job => job.RequestedByUserId, cancellationToken);
        // Agents are labelled Agent 1, Agent 2… in a stable order; the report never stores a name, email or user id.
        var agentLabels = requesters.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select((user, index) => (user, label: $"Agent {index + 1}")).ToDictionary(item => item.user, item => item.label, StringComparer.Ordinal);
        var posts = await LoadPostsAsync(organizationId, variants.Select(variant => variant.Id).ToArray(), cancellationToken);
        return variants.Where(variant => properties.ContainsKey(variant.PropertyId)).Select(variant =>
        {
            var property = properties[variant.PropertyId];
            var score = VerticalFormatRecommendationSet.FromSnapshotJson(variant.RecommendationSnapshotJson)?.Formats
                .FirstOrDefault(format => format.Format == variant.Format)?.Score ?? 0;
            var totals = VerticalMetricTotals.Sum((posts.GetValueOrDefault(variant.Id) ?? []).Select(post => post.Latest).OfType<CampaignMetricValues>().ToArray());
            return new VerticalRuleEvaluationInput(variant.Format, property.PropertyType, property.ListingPrice,
                photoCounts.GetValueOrDefault(variant.PropertyId), withVideo.Contains(variant.PropertyId),
                agentLabels.GetValueOrDefault(requesters.GetValueOrDefault(variant.CampaignGenerationJobId) ?? string.Empty) ?? "Agent ?",
                score, totals);
        }).ToArray();
    }

    private static VerticalRuleEvaluationView ToView(VerticalRuleEvaluationReport report) => new(report.Id, report.CreatedAtUtc,
        report.BaseRuleVersion, report.CalculationVersion, report.VideoCount, report.TotalViews, report.MeetsMinimumSample, report.Status,
        report.PromotedRuleVersion, report.PromotedAtUtc, VerticalPerformanceCalculator.FromJson(report.SegmentsJson));

    private async Task<Guid> AuthorizeAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers.AsNoTracking().Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to a workspace.");
        if (!await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, organizationId, cancellationToken))
        {
            throw new PerformanceTrackingDisabledException();
        }

        return organizationId;
    }

    private async Task<Guid> AuthorizeAdminAsync(string userId, CancellationToken cancellationToken)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required for the rule evaluation.");
        }

        return organizationId;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded vertical video post {DistributionId} on {Platform}")]
    private partial void LogDistributionAdded(Guid distributionId, DistributionPlatform platform);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Count} metric snapshots from {Source}")]
    private partial void LogSnapshotsAdded(int count, CampaignMetricSource source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected a metrics import with {Count} problems")]
    private partial void LogImportRejected(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created rule evaluation draft {ReportId} from {Videos} videos; minimum sample met: {Meets}")]
    private partial void LogDraftCreated(Guid reportId, int videos, bool meets);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rule evaluation {ReportId} promoted rule version {RuleVersion}")]
    private partial void LogPromoted(Guid reportId, string ruleVersion);
}
