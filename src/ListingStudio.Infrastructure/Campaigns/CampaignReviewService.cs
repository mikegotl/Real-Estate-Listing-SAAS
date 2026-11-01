using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class CampaignReviewService(ApplicationDbContext dbContext, UserManager<ApplicationUser> users,
    TimeProvider timeProvider) : ICampaignReviewService
{
    public async Task<IReadOnlyList<CampaignReviewItem>> ListAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        return await ListCoreAsync(organizationId, null, cancellationToken);
    }

    public async Task<IReadOnlyList<CampaignReviewItem>> ListSessionAsync(
        string userId, Guid suiteId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var suite = await dbContext.TikTokTestSuites.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == suiteId && s.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("This test session is unavailable in your workspace.");
        Guid[] jobIds = new Guid?[] { suite.ShortVoiceoverJobId, suite.MusicOnlyJobId, suite.FullVoiceoverJobId }
            .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        return await ListCoreAsync(organizationId, jobIds, cancellationToken);
    }

    private async Task<IReadOnlyList<CampaignReviewItem>> ListCoreAsync(
        Guid organizationId, Guid[]? sessionJobIds, CancellationToken cancellationToken)
    {
        // RequestedOutputs is stored through a string converter; SQL bitwise operators cannot inspect it.
        // Stream only this workspace's ordered jobs and stop after 100 matching vertical campaigns.
        var jobs = new List<CampaignGenerationJob>();
        // Creative format test campaigns belong to the Creative Format Testing tab, never to the render regression gallery.
        var creativeTestJobs = dbContext.VerticalCreativeTestCases
            .Where(item => item.OrganizationId == organizationId && item.CampaignGenerationJobId != null)
            .Select(item => item.CampaignGenerationJobId!.Value);
        var query = dbContext.CampaignGenerationJobs.AsNoTracking().Include(job => job.Property)
            .Where(job => job.OrganizationId == organizationId && !creativeTestJobs.Contains(job.Id));
        if (sessionJobIds is not null) query = query.Where(job => sessionJobIds.Contains(job.Id));
        await foreach (var job in query.OrderByDescending(job => job.CreatedAtUtc).ThenByDescending(job => job.Id)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (!job.RequestedOutputs.HasFlag(CampaignOutputSelection.TikTok))
            {
                continue;
            }
            jobs.Add(job);
            if (jobs.Count == 100)
            {
                break;
            }
        }
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var videos = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && jobIds.Contains(video.CampaignGenerationJobId)
                && video.Kind == CampaignOutputKind.TikTok && (video.CampaignVideoVariantId == null || video.CampaignVideoVariantId == video.Id)).ToListAsync(cancellationToken);
        var videoIds = videos.Select(video => video.Id).ToArray();
        var reviews = await dbContext.CampaignVideoReviews.AsNoTracking()
            .Where(review => review.OrganizationId == organizationId && videoIds.Contains(review.CampaignDeliverableId))
            .OrderByDescending(review => review.Sequence)
            .ToListAsync(cancellationToken);
        return jobs.Select(job =>
        {
            var video = videos.SingleOrDefault(candidate => candidate.CampaignGenerationJobId == job.Id);
            var history = reviews.Where(review => review.CampaignDeliverableId == video?.Id)
                .Select(review => new CampaignReviewEntry(review.Id, review.VideoVersion, review.Decision,
                    review.Feedback, review.CreatedAtUtc)).ToArray();
            var current = history.FirstOrDefault(review => review.Version == video?.Version);
            return new CampaignReviewItem(job.Id, video?.Id ?? Guid.Empty, job.PropertyId,
                $"{job.Property.Address1}, {job.Property.City}, {job.Property.State}", video?.Version ?? 0,
                job.Status, job.CurrentStage, job.LastError, video?.Status,
                $"{job.MarketingTemplate} · {job.Pace} · {job.ShortFormVoiceover} · {job.Soundtrack} · {job.SpokenLanguage}/{job.CaptionLanguage}",
                job.CreatedAtUtc, current?.Decision, history);
        }).ToArray();
    }

    public async Task DecideAsync(string userId, Guid jobId, int expectedVersion,
        CampaignVideoReviewDecision decision, string? feedback, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var deliverableId = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.CampaignGenerationJobId == jobId && candidate.Kind == CampaignOutputKind.TikTok
                && (candidate.CampaignVideoVariantId == null || candidate.CampaignVideoVariantId == candidate.Id))
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("This campaign video is unavailable in your workspace.");
        await DecideCoreAsync(userId, organizationId, deliverableId, expectedVersion, decision, feedback, cancellationToken);
    }

    public async Task DecideDeliverableAsync(string userId, Guid deliverableId, int expectedVersion,
        CampaignVideoReviewDecision decision, string? feedback, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        await DecideCoreAsync(userId, organizationId, deliverableId, expectedVersion, decision, feedback, cancellationToken);
    }

    private async Task DecideCoreAsync(string userId, Guid organizationId, Guid deliverableId, int expectedVersion,
        CampaignVideoReviewDecision decision, string? feedback, CancellationToken cancellationToken)
    {
        var video = await dbContext.CampaignDeliverables.AsNoTracking()
            .Include(candidate => candidate.CampaignGenerationJob)
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId && candidate.Id == deliverableId,
                cancellationToken)
            ?? throw new KeyNotFoundException("This campaign video is unavailable in your workspace.");
        CampaignVideoReviewRules.EnsureReviewable(video, expectedVersion);
        dbContext.CampaignVideoReviews.Add(CampaignVideoReview.Create(organizationId, video.Id,
            expectedVersion, decision, userId, feedback, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> AuthorizeAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required.");
        }
        return await dbContext.OrganizationMembers.AsNoTracking().Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The administrator does not belong to a workspace.");
    }
}
