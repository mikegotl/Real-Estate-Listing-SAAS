using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>Media roles for the signed-in user's workspace. Logs nothing about the property.</summary>
public sealed class VerticalMediaRoleService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    : IVerticalMediaRoleService
{
    public async Task<IReadOnlyList<VerticalMediaRoleItem>?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        if (!await VerticalPlanningInputLoader.PropertyExistsAsync(dbContext, organizationId, propertyId, cancellationToken))
        {
            return null;
        }

        var (photos, footage) = await VerticalPlanningInputLoader.LoadMediaAsync(
            dbContext, organizationId, propertyId, cancellationToken);
        return photos.Select(photo => new VerticalMediaRoleItem(photo.MediaId, false, photo.Stop, photo.Roles))
            .Concat(footage.Select(video => new VerticalMediaRoleItem(video.VideoId, true, null, video.Roles)))
            .ToArray();
    }

    public async Task<bool> SetAsync(
        string userId,
        Guid propertyId,
        Guid targetId,
        VerticalMediaRole role,
        VerticalMediaRoleDecision decision,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        if (!await VerticalPlanningInputLoader.PropertyExistsAsync(dbContext, organizationId, propertyId, cancellationToken))
        {
            return false;
        }

        var isPhoto = await dbContext.PropertyMedia.AsNoTracking().AnyAsync(media => media.Id == targetId
            && media.OrganizationId == organizationId
            && media.PropertyId == propertyId,
            cancellationToken);
        var isVideo = !isPhoto && await dbContext.PropertyVideos.AsNoTracking().AnyAsync(video => video.Id == targetId
            && video.OrganizationId == organizationId
            && video.PropertyId == propertyId,
            cancellationToken);
        if (!isPhoto && !isVideo)
        {
            return false;
        }

        dbContext.PropertyMediaRoleAssignments.Add(PropertyMediaRoleAssignment.Create(
            organizationId,
            propertyId,
            isPhoto ? targetId : null,
            isVideo ? targetId : null,
            role,
            decision,
            userId,
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<VerticalMediaRoleHistoryEntry>> GetHistoryAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        var rows = await dbContext.PropertyMediaRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.OrganizationId == organizationId && assignment.PropertyId == propertyId)
            .OrderByDescending(assignment => assignment.SetAtUtc)
            .ThenByDescending(assignment => assignment.Id)
            .Select(assignment => new
            {
                Target = assignment.PropertyMediaId ?? assignment.PropertyVideoId ?? Guid.Empty,
                assignment.Role,
                assignment.Decision,
                assignment.SetByUserId,
                assignment.SetAtUtc,
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(row => new VerticalMediaRoleHistoryEntry(row.Target, row.Role, row.Decision, row.SetByUserId, row.SetAtUtc))
            .ToArray();
    }
}

/// <summary>Agent-confirmed claims for the signed-in user's workspace. Logs nothing about the claim text.</summary>
public sealed class ListingCreativeClaimService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    : IListingCreativeClaimService
{
    public async Task<IReadOnlyList<ListingCreativeClaimState>?> GetActiveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        if (!await VerticalPlanningInputLoader.PropertyExistsAsync(dbContext, organizationId, propertyId, cancellationToken))
        {
            return null;
        }

        var claims = await VerticalPlanningInputLoader.LoadActiveClaimsAsync(
            dbContext, organizationId, propertyId, timeProvider.GetUtcNow(), cancellationToken);
        return claims.Select(claim => new ListingCreativeClaimState(
                claim.Kind, claim.PrimaryText, claim.SecondaryText, claim.ValidUntilUtc, claim.ConfirmedByUserId, claim.ConfirmedAtUtc))
            .ToArray();
    }

    public Task<bool> ConfirmStoryAsync(
        string userId,
        Guid propertyId,
        string problem,
        string payoff,
        CancellationToken cancellationToken = default) =>
        AddAsync(userId, propertyId, (organizationId, now) =>
            ListingCreativeClaim.ConfirmStory(organizationId, propertyId, problem, payoff, userId, now), cancellationToken);

    public Task<bool> ConfirmAccessAsync(
        string userId,
        Guid propertyId,
        string accessText,
        DateTimeOffset? validUntilUtc,
        CancellationToken cancellationToken = default) =>
        AddAsync(userId, propertyId, (organizationId, now) =>
            ListingCreativeClaim.ConfirmAccess(organizationId, propertyId, accessText, validUntilUtc, userId, now), cancellationToken);

    public Task<bool> WithdrawAsync(
        string userId,
        Guid propertyId,
        ListingCreativeClaimKind kind,
        CancellationToken cancellationToken = default) =>
        AddAsync(userId, propertyId, (organizationId, now) =>
            ListingCreativeClaim.Withdraw(organizationId, propertyId, kind, userId, now), cancellationToken);

    private async Task<bool> AddAsync(
        string userId,
        Guid propertyId,
        Func<Guid, DateTimeOffset, ListingCreativeClaim> create,
        CancellationToken cancellationToken)
    {
        var organizationId = await VerticalPlanningInputLoader.OrganizationOfAsync(dbContext, userId, cancellationToken);
        if (!await VerticalPlanningInputLoader.PropertyExistsAsync(dbContext, organizationId, propertyId, cancellationToken))
        {
            return false;
        }

        dbContext.ListingCreativeClaims.Add(create(organizationId, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
