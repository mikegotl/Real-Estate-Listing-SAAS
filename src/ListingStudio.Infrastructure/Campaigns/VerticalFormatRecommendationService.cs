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
public sealed class VerticalFormatRecommendationService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    : IVerticalFormatRecommendationService, IVerticalCreativePlanningService
{
    public async Task<VerticalFormatRecommendationSet?> RecommendAsync(
        string userId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default) =>
        (await PlanAsync(userId, propertyId, context, cancellationToken))?.Recommendation;

    public async Task<VerticalFormatRecommendationSet?> RecommendForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default) =>
        (await PlanForOrganizationAsync(organizationId, propertyId, context, cancellationToken))?.Recommendation;

    public async Task<VerticalCreativePlan?> PlanAsync(
        string userId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        return await PlanForOrganizationAsync(organizationId, propertyId, context, cancellationToken);
    }

    public async Task<VerticalCreativePlan?> PlanForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = await VerticalPlanningInputLoader.LoadAsync(
            dbContext, organizationId, propertyId, context, timeProvider.GetUtcNow(), cancellationToken);
        return input is null ? null : VerticalCreativePlan.Create(input);
    }
}

/// <summary>Builds <see cref="VerticalPlanningInput"/> from the database, every query scoped to one workspace.</summary>
internal static class VerticalPlanningInputLoader
{
    public static async Task<Guid> OrganizationOfAsync(ApplicationDbContext dbContext, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    public static Task<bool> PropertyExistsAsync(
        ApplicationDbContext dbContext, Guid organizationId, Guid propertyId, CancellationToken cancellationToken) =>
        dbContext.Properties.AsNoTracking().AnyAsync(property => property.Id == propertyId
            && property.OrganizationId == organizationId
            && property.ArchivedAtUtc == null,
            cancellationToken);

    /// <summary>Storyboard photos (not Extra photos) and property videos, with roles resolved.</summary>
    public static async Task<(VerticalPlanningPhoto[] Photos, VerticalPlanningFootage[] Footage)> LoadMediaAsync(
        ApplicationDbContext dbContext, Guid organizationId, Guid propertyId, CancellationToken cancellationToken)
    {
        var photos = await dbContext.PropertyMedia
            .AsNoTracking()
            .Where(media => media.OrganizationId == organizationId
                && media.PropertyId == propertyId
                && !media.IsInGallery
                && media.WalkthroughStop != null)
            .OrderBy(media => media.DisplayOrder)
            .ThenBy(media => media.Id)
            .Select(media => new
            {
                media.Id,
                media.WalkthroughStop,
                media.Category,
                Completed = media.AnalysisStatus == PropertyMediaAnalysisStatus.Completed,
                media.QualityScore,
                media.HeroScore,
                media.IsExterior,
                media.Width,
                media.Height,
                media.DisplayOrder,
            })
            .ToArrayAsync(cancellationToken);
        var videos = await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId && video.PropertyId == propertyId)
            .OrderBy(video => video.TourOrder)
            .ThenBy(video => video.Id)
            .Select(video => new
            {
                video.Id,
                DurationMs = video.EnhancedDurationMs ?? video.DurationMs,
                Ready = video.ProcessingStatus == PropertyVideoProcessingStatus.Completed,
                video.HasAudio,
                video.TourStop,
                video.TourOrder,
            })
            .ToArrayAsync(cancellationToken);
        var overrides = (await dbContext.PropertyMediaRoleAssignments
                .AsNoTracking()
                .Where(assignment => assignment.OrganizationId == organizationId && assignment.PropertyId == propertyId)
                .Select(assignment => new
                {
                    Target = assignment.PropertyMediaId ?? assignment.PropertyVideoId ?? Guid.Empty,
                    assignment.Role,
                    assignment.Decision,
                    assignment.SetByUserId,
                    assignment.SetAtUtc,
                })
                .ToArrayAsync(cancellationToken))
            .Select(row => new VerticalMediaRoleOverride(row.Target, row.Role, row.Decision, row.SetByUserId, row.SetAtUtc))
            .ToLookup(row => row.TargetId);
        var suggested = VerticalMediaRoles.Suggest(
            photos.Select(photo => new VerticalRolePhoto(photo.Id, photo.Category, photo.Completed, photo.QualityScore,
                photo.HeroScore, photo.IsExterior, photo.DisplayOrder)).ToArray(),
            videos.Select(video => new VerticalRoleFootage(video.Id, video.DurationMs, video.Ready, video.HasAudio, video.TourStop))
                .ToArray());
        IReadOnlyList<VerticalMediaRoleState> RolesOf(Guid id) =>
            VerticalMediaRoles.Resolve(suggested.TryGetValue(id, out var roles) ? roles : null, overrides[id]);

        return (
            photos.Select(photo => new VerticalPlanningPhoto(photo.Id, photo.WalkthroughStop, photo.Category, photo.Completed,
                photo.QualityScore, photo.HeroScore, photo.IsExterior, photo.Width, photo.Height, photo.DisplayOrder,
                RolesOf(photo.Id))).ToArray(),
            videos.Select(video => new VerticalPlanningFootage(video.Id, video.DurationMs, video.Ready, video.HasAudio,
                video.TourStop, video.TourOrder, RolesOf(video.Id))).ToArray());
    }

    /// <summary>The latest claim of each kind that is still in force.</summary>
    public static async Task<IReadOnlyList<ListingCreativeClaim>> LoadActiveClaimsAsync(
        ApplicationDbContext dbContext, Guid organizationId, Guid propertyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ListingCreativeClaims
            .AsNoTracking()
            .Where(claim => claim.OrganizationId == organizationId && claim.PropertyId == propertyId)
            .ToArrayAsync(cancellationToken);
        return rows
            .GroupBy(claim => claim.Kind)
            // Latest decision wins. A withdrawal recorded in the same instant as a confirmation wins the tie, so an
            // ambiguous history never unlocks a claim.
            .Select(group => group.OrderByDescending(claim => claim.ConfirmedAtUtc)
                .ThenByDescending(claim => claim.IsWithdrawn)
                .ThenByDescending(claim => claim.Id)
                .First())
            .Where(claim => claim.IsActiveAt(now))
            .OrderBy(claim => claim.Kind)
            .ToArray();
    }

    public static async Task<VerticalPlanningInput?> LoadAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        DateTimeOffset now,
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

        var (photos, footage) = await LoadMediaAsync(dbContext, organizationId, propertyId, cancellationToken);
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
        var claims = await LoadActiveClaimsAsync(dbContext, organizationId, propertyId, now, cancellationToken);
        var storyClaim = claims.FirstOrDefault(claim => claim.Kind == ListingCreativeClaimKind.StoryReveal);
        var accessClaim = claims.FirstOrDefault(claim => claim.Kind == ListingCreativeClaimKind.ExclusiveAccess);

        return new VerticalPlanningInput(
            propertyId,
            new VerticalListingFacts(
                property.PropertyType,
                property.ListingPrice,
                property.Bedrooms,
                property.Bathrooms,
                property.SquareFeet,
                property.LotSize,
                property.City,
                property.State,
                approvedPlaces),
            photos,
            footage,
            narration is null
                ? []
                : narration.Segments
                    .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                    .Select(segment => new VerticalNarrationLine(segment.Key, segment.Text.Trim(), segment.WalkthroughStop))
                    .ToArray(),
            new VerticalPresenterInput(hasPortrait, hasPortrait && context.PresenterConsentConfirmed),
            context.ValuePositioningConfirmed ? context.VerifiedPriceChangeAmount : null,
            storyClaim is { PrimaryText: { } problem, SecondaryText: { } payoff }
                ? new VerticalStoryClaimInput(storyClaim.Id, problem, payoff)
                : null,
            accessClaim is { PrimaryText: { } access } ? new VerticalAccessClaimInput(accessClaim.Id, access) : null,
            VerticalUnconfirmedMentions.Find(
                [property.Description, story?.OpeningHook, story?.PropertyNarrative, story?.VoiceoverScript]),
            VerticalStoryLines.From(story?.VoiceoverScript));
    }
}
