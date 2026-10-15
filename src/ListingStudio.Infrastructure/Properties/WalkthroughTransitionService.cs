using System.Data;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class WalkthroughTransitionService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage campaignAssetStorage,
    IWalkthroughVideoProvider provider,
    TimeProvider timeProvider) : IWalkthroughTransitionService
{
    public bool IsEnabled => provider.IsEnabled;

    public async Task<IReadOnlyList<WalkthroughTransitionItem>> ListAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var pairs = await LoadPairsAsync(organizationId, propertyId, cancellationToken);
        if (pairs.Count == 0)
        {
            return [];
        }

        var transitions = await LoadTransitionsAsync(organizationId, propertyId, tracking: false, cancellationToken);
        var currentVersion = provider.GenerationVersion;
        return pairs.Select(pair =>
        {
            var candidates = transitions
                .Where(transition => transition.FromPropertyMediaId == pair.From
                    && transition.ToPropertyMediaId == pair.To)
                .ToArray();
            // A finished clip from an earlier generation version still plays until the current version replaces it.
            var chosen = candidates.FirstOrDefault(transition => transition.GenerationVersion == currentVersion
                    && transition.Status == WalkthroughTransitionStatus.Completed)
                ?? candidates.FirstOrDefault(transition => transition.GenerationVersion == currentVersion
                    && transition.Status != WalkthroughTransitionStatus.Failed)
                ?? candidates.Where(transition => transition.Status == WalkthroughTransitionStatus.Completed)
                    .OrderByDescending(transition => transition.CompletedAtUtc)
                    .FirstOrDefault()
                ?? candidates.FirstOrDefault(transition => transition.GenerationVersion == currentVersion);
            return new WalkthroughTransitionItem(
                pair.From,
                pair.To,
                chosen?.Id,
                chosen?.Status,
                chosen?.Status == WalkthroughTransitionStatus.Failed ? chosen.LastError : null);
        }).ToArray();
    }

    public async Task<int> QueueAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Walk-through clip generation is not configured.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await dbContext.Properties.AnyAsync(
                property => property.Id == propertyId
                    && property.OrganizationId == organizationId
                    && property.ArchivedAtUtc == null,
                cancellationToken))
        {
            throw new InvalidOperationException("The property is unavailable for walk-through clips.");
        }

        var pairs = await LoadPairsAsync(organizationId, propertyId, cancellationToken);
        var version = provider.GenerationVersion;
        var existing = (await LoadTransitionsAsync(organizationId, propertyId, tracking: true, cancellationToken))
            .Where(transition => transition.GenerationVersion == version)
            .ToDictionary(transition => (transition.FromPropertyMediaId, transition.ToPropertyMediaId));
        var queued = 0;
        var now = timeProvider.GetUtcNow();
        foreach (var (from, to) in pairs)
        {
            if (!existing.TryGetValue((from, to), out var transition))
            {
                dbContext.WalkthroughTransitions.Add(
                    WalkthroughTransition.Create(organizationId, propertyId, from, to, version, now));
                queued++;
            }
            else if (transition.Status == WalkthroughTransitionStatus.Failed && !transition.CanRetry)
            {
                transition.Requeue();
                queued++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return queued;
    }

    public async Task<WalkthroughTransitionContent?> OpenReadAsync(
        string userId,
        Guid transitionId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var transition = await dbContext.WalkthroughTransitions.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == transitionId
                && candidate.OrganizationId == organizationId
                && candidate.Status == WalkthroughTransitionStatus.Completed,
            cancellationToken);
        if (transition?.AssetPath is null || transition.ContentType is null)
        {
            return null;
        }

        var content = await campaignAssetStorage.OpenReadAsync(transition.AssetPath, cancellationToken);
        return content is null ? null : new WalkthroughTransitionContent(content, transition.ContentType);
    }

    private async Task<IReadOnlyList<(Guid From, Guid To)>> LoadPairsAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var shots = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .Select(media => new { media.Id, media.WalkthroughStop, media.DisplayOrder })
            .ToArrayAsync(cancellationToken);
        return WalkthroughStoryboard.ShotPairs(shots, shot => shot.WalkthroughStop, shot => shot.DisplayOrder)
            .Select(pair => (pair.From.Id, pair.To.Id))
            .ToArray();
    }

    private Task<List<WalkthroughTransition>> LoadTransitionsAsync(
        Guid organizationId,
        Guid propertyId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WalkthroughTransitions
            .Where(transition => transition.OrganizationId == organizationId && transition.PropertyId == propertyId);
        return (tracking ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
    }

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }
}
