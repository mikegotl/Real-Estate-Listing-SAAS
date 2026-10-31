using System.Collections.Concurrent;
using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Administration;

/// <summary>Per-process cache of saved feature switches, shared by every scope in the host.</summary>
public sealed class FeatureStateCache
{
    private readonly ConcurrentDictionary<string, (FeatureFlagState State, DateTimeOffset ExpiresAtUtc)> entries =
        new(StringComparer.Ordinal);

    public bool TryGet(string key, DateTimeOffset now, out FeatureFlagState state)
    {
        if (entries.TryGetValue(key, out var entry) && entry.ExpiresAtUtc > now)
        {
            state = entry.State;
            return true;
        }

        state = FeatureFlagState.Off(key);
        return false;
    }

    public void Set(FeatureFlagState state, DateTimeOffset now) =>
        entries[state.Key] = (state, now + IFeatureStateService.CacheLifetime);

    public void Invalidate(string key) => entries.TryRemove(key, out _);
}

public sealed class FeatureStateService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> users,
    FeatureStateCache cache,
    TimeProvider timeProvider) : IFeatureStateService
{
    public async Task<bool> IsEnabledAsync(
        string featureKey,
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        organizationId != Guid.Empty && (await GetAsync(featureKey, cancellationToken)).IsEnabledFor(organizationId);

    public async Task<FeatureFlagState> GetAsync(string featureKey, CancellationToken cancellationToken = default)
    {
        if (!FeatureFlagKeys.IsKnown(featureKey))
        {
            throw new ArgumentException("Unknown feature.", nameof(featureKey));
        }

        var now = timeProvider.GetUtcNow();
        if (cache.TryGet(featureKey, now, out var cached))
        {
            return cached;
        }

        var flag = await dbContext.FeatureFlags.AsNoTracking()
            .Include(candidate => candidate.Organizations)
            .SingleOrDefaultAsync(candidate => candidate.Key == featureKey, cancellationToken);
        var state = flag is null ? FeatureFlagState.Off(featureKey) : ToState(flag);
        cache.Set(state, now);
        return state;
    }

    public async Task<FeatureFlagState> ConfigureAsync(
        string userId,
        string featureKey,
        bool globallyEnabled,
        bool enabledForAllOrganizations,
        IReadOnlyCollection<Guid> pilotOrganizationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(pilotOrganizationIds);
        if (!FeatureFlagKeys.IsKnown(featureKey))
        {
            throw new ArgumentException("Unknown feature.", nameof(featureKey));
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required.");
        }

        var wanted = pilotOrganizationIds.Where(id => id != Guid.Empty).ToHashSet();
        var known = await dbContext.Organizations.AsNoTracking()
            .Where(organization => wanted.Contains(organization.Id))
            .Select(organization => organization.Id)
            .ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            throw new ArgumentException("Every pilot workspace must exist.", nameof(pilotOrganizationIds));
        }

        var now = timeProvider.GetUtcNow();
        var flag = await dbContext.FeatureFlags.Include(candidate => candidate.Organizations)
            .SingleOrDefaultAsync(candidate => candidate.Key == featureKey, cancellationToken);
        if (flag is null)
        {
            flag = FeatureFlag.Create(featureKey, now);
            dbContext.FeatureFlags.Add(flag);
        }

        flag.Configure(globallyEnabled, enabledForAllOrganizations, userId, now);
        flag.Organizations.RemoveAll(item => !wanted.Contains(item.OrganizationId));
        foreach (var organizationId in wanted.Where(id => flag.Organizations.All(item => item.OrganizationId != id)))
        {
            flag.Organizations.Add(FeatureFlagOrganization.Create(featureKey, organizationId, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Invalidate(featureKey);
        return ToState(flag);
    }

    private static FeatureFlagState ToState(FeatureFlag flag) => new(
        flag.Key,
        flag.IsGloballyEnabled,
        flag.IsEnabledForAllOrganizations,
        flag.Organizations.Select(item => item.OrganizationId).Order().ToArray(),
        flag.UpdatedAtUtc);
}
