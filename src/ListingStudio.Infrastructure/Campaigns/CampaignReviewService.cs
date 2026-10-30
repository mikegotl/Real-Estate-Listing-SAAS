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
        var jobs = await dbContext.CampaignGenerationJobs.AsNoTracking().Include(job => job.Property)
            .Where(job => job.OrganizationId == organizationId
                && (job.RequestedOutputs & CampaignOutputSelection.TikTok) != 0)
            .OrderByDescending(job => job.CreatedAtUtc).ThenByDescending(job => job.Id)
            .Take(100).ToListAsync(cancellationToken);
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var videos = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && jobIds.Contains(video.CampaignGenerationJobId)
                && video.Kind == CampaignOutputKind.TikTok).ToListAsync(cancellationToken);
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
        var video = await dbContext.CampaignDeliverables.AsNoTracking()
            .Include(candidate => candidate.CampaignGenerationJob)
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId
                && candidate.CampaignGenerationJobId == jobId && candidate.Kind == CampaignOutputKind.TikTok,
                cancellationToken);
        if (video is null)
        {
            throw new KeyNotFoundException("This campaign video is unavailable in your workspace.");
        }
        if (video.Version != expectedVersion)
        {
            throw new InvalidOperationException("The video changed. Refresh and watch the current version before reviewing it.");
        }
        if (video.Status != CampaignDeliverableStatus.Rendered
            || video.CampaignGenerationJob.Status != CampaignGenerationStatus.Completed)
        {
            throw new InvalidOperationException("Wait for the campaign to complete before reviewing its video.");
        }
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
