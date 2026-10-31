using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Properties;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Loads one listing's stored facts, scoped to its workspace, and runs <see cref="VerticalFormatRules"/>. Reads only;
/// logs nothing about the property.
/// </summary>
public sealed class VerticalFormatRecommendationService(ApplicationDbContext dbContext)
    : IVerticalFormatRecommendationService
{
    public async Task<VerticalFormatRecommendationSet?> RecommendAsync(
        string userId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
        return await RecommendForOrganizationAsync(organizationId, propertyId, context, cancellationToken);
    }

    public async Task<VerticalFormatRecommendationSet?> RecommendForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var inputs = await LoadInputsAsync(organizationId, propertyId, context, cancellationToken);
        return inputs is null ? null : VerticalFormatRules.Evaluate(inputs);
    }

    internal async Task<VerticalFormatInputs?> LoadInputsAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken)
    {
        var property = await dbContext.Properties
            .AsNoTracking()
            .Where(candidate => candidate.Id == propertyId
                && candidate.OrganizationId == organizationId
                && candidate.ArchivedAtUtc == null)
            .Select(candidate => new
            {
                candidate.PropertyType,
                candidate.ListingPrice,
                candidate.Bedrooms,
                candidate.Bathrooms,
                candidate.SquareFeet,
                candidate.LotSize,
                candidate.City,
                candidate.State,
                candidate.Description,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            return null;
        }

        // The same photos a campaign renders: placed on the storyboard, not parked in Extra photos.
        var photos = await dbContext.PropertyMedia
            .AsNoTracking()
            .Where(media => media.OrganizationId == organizationId
                && media.PropertyId == propertyId
                && !media.IsInGallery
                && media.WalkthroughStop != null)
            .OrderBy(media => media.DisplayOrder)
            .ThenBy(media => media.Id)
            .Select(media => new VerticalPhotoInput(
                media.Id,
                media.Category,
                media.AnalysisStatus == PropertyMediaAnalysisStatus.Completed,
                media.QualityScore,
                media.HeroScore,
                media.Width,
                media.Height,
                VerticalPhotoStoryRole.None))
            .ToArrayAsync(cancellationToken);
        var footage = await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && video.PropertyId == propertyId)
            .OrderBy(video => video.TourOrder)
            .ThenBy(video => video.Id)
            .Select(video => new VerticalFootageInput(
                video.Id,
                video.EnhancedDurationMs ?? video.DurationMs,
                video.ProcessingStatus == PropertyVideoProcessingStatus.Completed))
            .ToArrayAsync(cancellationToken);
        var approvedPlaces = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .CountAsync(insight => insight.OrganizationId == organizationId
                && insight.PropertyId == propertyId
                && insight.IsApproved,
                cancellationToken);
        var narration = await UserNarrationSource.LoadAsync(dbContext, organizationId, propertyId, cancellationToken);
        var hasPortrait = context.AgentPortraitId is { } portraitId
            && await dbContext.AgentPortraits.AsNoTracking().AnyAsync(
                portrait => portrait.Id == portraitId && portrait.OrganizationId == organizationId,
                cancellationToken);
        var story = await dbContext.PropertyStories
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => new { candidate.OpeningHook, candidate.PropertyNarrative, candidate.VoiceoverScript })
            .FirstOrDefaultAsync(cancellationToken);

        return new VerticalFormatInputs(
            property.PropertyType,
            property.ListingPrice,
            property.Bedrooms,
            property.Bathrooms,
            property.SquareFeet,
            property.LotSize,
            !string.IsNullOrWhiteSpace(property.City) && !string.IsNullOrWhiteSpace(property.State),
            approvedPlaces,
            photos,
            footage,
            narration is { Segments.Count: > 0 },
            new VerticalPresenterInput(hasPortrait, hasPortrait && context.PresenterConsentConfirmed),
            new VerticalAgentConfirmations(ValuePositioning: context.ValuePositioningConfirmed),
            VerticalUnconfirmedMentions.Find(
                [property.Description, story?.OpeningHook, story?.PropertyNarrative, story?.VoiceoverScript]));
    }
}
