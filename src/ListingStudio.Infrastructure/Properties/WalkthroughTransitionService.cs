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
    /// <summary>How long a clip queued by a storyboard edit waits, so quick successive edits don't pay for clips.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(45);

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
        return pairs.Select(pair =>
        {
            var currentVersion = pair.Version;
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
                chosen?.Status == WalkthroughTransitionStatus.Failed ? chosen.LastError : null,
                chosen is null || chosen.GenerationVersion == currentVersion);
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
        return await QueueMissingAsync(organizationId, propertyId, automatic: false, cancellationToken)
            ?? throw new InvalidOperationException("The property is unavailable for walk-through clips.");
    }

    public async Task<int> RequeueAffectedAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await QueueMissingAsync(organizationId, propertyId, automatic: true, cancellationToken) ?? 0;
    }

    public async Task<int> RequeueAffectedForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property identities are required.");
        }

        return IsEnabled
            ? await QueueMissingAsync(organizationId, propertyId, automatic: true, cancellationToken) ?? 0
            : 0;
    }

    // Queues a clip for each current pair without one. An automatic run only acts for a property whose clips the
    // agent has already requested, waits SettleDelay before submitting, and leaves clips that failed for good alone.
    // Returns null when the property is unavailable.
    private async Task<int?> QueueMissingAsync(
        Guid organizationId,
        Guid propertyId,
        bool automatic,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await dbContext.Properties.AnyAsync(
                property => property.Id == propertyId
                    && property.OrganizationId == organizationId
                    && property.ArchivedAtUtc == null,
                cancellationToken))
        {
            return null;
        }

        var transitions = await LoadTransitionsAsync(organizationId, propertyId, tracking: true, cancellationToken);
        if (automatic && transitions.Count == 0)
        {
            return 0;
        }

        var pairs = await LoadPairsAsync(organizationId, propertyId, cancellationToken);
        var existing = transitions.ToDictionary(transition => (
            transition.FromPropertyMediaId,
            transition.ToPropertyMediaId,
            transition.GenerationVersion));
        var queued = 0;
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? notBefore = automatic ? now.Add(SettleDelay) : null;
        foreach (var pair in pairs)
        {
            if (!existing.TryGetValue((pair.From, pair.To, pair.Version), out var transition))
            {
                dbContext.WalkthroughTransitions.Add(WalkthroughTransition.Create(
                    organizationId,
                    propertyId,
                    pair.From,
                    pair.To,
                    pair.Version,
                    now,
                    notBefore));
                queued++;
            }
            else if (!automatic && transition.Status == WalkthroughTransitionStatus.Failed && !transition.CanRetry)
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

    private Task<IReadOnlyList<WalkthroughShotPair>> LoadPairsAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken) =>
        WalkthroughShotPairs.LoadAsync(dbContext, provider, organizationId, propertyId, cancellationToken);

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
