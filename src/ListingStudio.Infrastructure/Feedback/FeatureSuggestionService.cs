using ListingStudio.Application.Feedback;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Feedback;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Feedback;

public sealed class FeatureSuggestionService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IFeatureSuggestionService
{
    public async Task<FeatureSuggestionReceipt?> SubmitAsync(
        string userId,
        Guid? propertyId,
        FeatureSuggestionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(input);

        var organizationId = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => member.OrganizationId)
            .SingleAsync(cancellationToken);
        if (propertyId is not null)
        {
            var propertyExists = await dbContext.Properties.AsNoTracking().AnyAsync(
                property => property.Id == propertyId && property.OrganizationId == organizationId,
                cancellationToken);
            if (!propertyExists)
            {
                return null;
            }
        }

        Guid? campaignId = null;
        if (input.RequiredForActiveCampaign)
        {
            if (propertyId is null)
            {
                throw new InvalidOperationException("Only a comment sent from a property can be linked to its active campaign.");
            }

            campaignId = await dbContext.CampaignGenerationJobs.AsNoTracking()
                .Where(job => job.OrganizationId == organizationId
                    && job.PropertyId == propertyId
                    && (job.Status == CampaignGenerationStatus.Queued
                        || job.Status == CampaignGenerationStatus.Running))
                .OrderByDescending(job => job.CreatedAtUtc)
                .Select(job => (Guid?)job.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (campaignId is null)
            {
                throw new InvalidOperationException(
                    "This property does not have an active campaign. Submit the suggestion without the campaign requirement, or start a campaign first.");
            }
        }

        var suggestion = FeatureSuggestion.Create(
            organizationId,
            propertyId,
            userId,
            input.Comment,
            input.Type,
            input.RequiredForActiveCampaign,
            campaignId,
            timeProvider.GetUtcNow(),
            input.PagePath);
        dbContext.FeatureSuggestions.Add(suggestion);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new FeatureSuggestionReceipt(
            suggestion.Id,
            suggestion.PropertyId,
            suggestion.Type,
            suggestion.RequiredForActiveCampaign,
            suggestion.CampaignGenerationJobId,
            suggestion.SubmittedAtUtc);
    }
}
