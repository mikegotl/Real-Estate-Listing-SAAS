namespace ListingStudio.Infrastructure.Billing;

using ListingStudio.Application.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;

/// <summary>
/// The campaigns that count against testers' monthly limits: each property a tester started a campaign for this
/// calendar month (UTC) counts once, so regenerating it is free, and failed or cancelled campaigns don't count.
/// </summary>
internal static class TesterCampaignUsage
{
    public static IQueryable<CampaignGenerationJob> CountedJobs(
        ApplicationDbContext dbContext,
        IReadOnlyCollection<string> userIds,
        DateTimeOffset now)
    {
        var monthStart = PlatformTesters.MonthStart(now);
        return dbContext.CampaignGenerationJobs
            .Where(job => userIds.Contains(job.RequestedByUserId)
                && job.CreatedAtUtc >= monthStart
                && (job.Status == CampaignGenerationStatus.Queued
                    || job.Status == CampaignGenerationStatus.Running
                    || job.Status == CampaignGenerationStatus.Completed));
    }
}
