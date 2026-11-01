using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Administration;

/// <summary>
/// Release switches for platform administrators. Suggested pilots are the workspaces of Testers and platform
/// administrators, the first audience for a controlled release.
/// </summary>
public sealed class FeatureRolloutService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> users,
    IFeatureStateService features,
    TimeProvider timeProvider) : IFeatureRolloutService
{
    public async Task<FeatureRolloutView> GetAsync(string userId, string featureKey, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId);
        return await ViewAsync(await features.GetAsync(featureKey, cancellationToken), cancellationToken);
    }

    public async Task<FeatureRolloutView> SaveAsync(string userId, string featureKey, bool globallyEnabled, bool enabledForAllOrganizations,
        IReadOnlyCollection<Guid> pilotOrganizationIds, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId);
        ArgumentNullException.ThrowIfNull(pilotOrganizationIds);
        var state = await features.ConfigureAsync(userId, featureKey, globallyEnabled, enabledForAllOrganizations, pilotOrganizationIds,
            cancellationToken);
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            FeatureRolloutText.AuditAction,
            "platform-admin",
            featureKey,
            FeatureRolloutText.AuditOutcome(state.IsGloballyEnabled, state.IsEnabledForAllOrganizations, state.PilotOrganizationIds.Count),
            timeProvider.GetUtcNow(),
            AuditPseudonym.For(userId)));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ViewAsync(state, cancellationToken);
    }

    public async Task<IReadOnlyList<FeatureWorkspaceChoice>> SearchWorkspacesAsync(string userId, string query, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId);
        var text = (query ?? string.Empty).Trim();
        if (text.Length < 2)
        {
            return [];
        }

        var pattern = $"%{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
        var ids = await dbContext.Organizations.AsNoTracking()
            .Where(organization => EF.Functions.ILike(organization.Name, pattern))
            .OrderBy(organization => organization.Name).Take(20)
            .Select(organization => organization.Id)
            .ToListAsync(cancellationToken);
        return await ChoicesAsync(ids, new Dictionary<Guid, string>(), cancellationToken);
    }

    private async Task<FeatureRolloutView> ViewAsync(FeatureFlagState state, CancellationToken cancellationToken)
    {
        var reasons = await SuggestedReasonsAsync(cancellationToken);
        var pilots = await ChoicesAsync(state.PilotOrganizationIds.ToList(), reasons, cancellationToken);
        var suggested = await ChoicesAsync(reasons.Keys.Where(id => !state.PilotOrganizationIds.Contains(id)).ToList(), reasons, cancellationToken);
        return new FeatureRolloutView(state, pilots, suggested);
    }

    /// <summary>Workspaces with a Tester or platform administrator member, and why.</summary>
    private async Task<Dictionary<Guid, string>> SuggestedReasonsAsync(CancellationToken cancellationToken)
    {
        var reasons = new Dictionary<Guid, string>();
        foreach (var (role, label) in new[] { (PlatformAdministration.Role, "Platform admin"), (PlatformTesters.Role, "Tester") })
        {
            var members = (await users.GetUsersInRoleAsync(role)).Select(user => user.Id).ToArray();
            var organizations = await dbContext.OrganizationMembers.AsNoTracking()
                .Where(member => members.Contains(member.UserId))
                .Select(member => member.OrganizationId).Distinct()
                .ToListAsync(cancellationToken);
            foreach (var organizationId in organizations)
            {
                reasons[organizationId] = reasons.TryGetValue(organizationId, out var existing) ? $"{existing}, {label}" : label;
            }
        }

        return reasons;
    }

    private async Task<IReadOnlyList<FeatureWorkspaceChoice>> ChoicesAsync(List<Guid> ids, IReadOnlyDictionary<Guid, string> reasons,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await dbContext.Organizations.AsNoTracking()
            .Where(organization => ids.Contains(organization.Id))
            .Select(organization => new
            {
                organization.Id,
                organization.Name,
                Members = dbContext.OrganizationMembers.Count(member => member.OrganizationId == organization.Id),
            })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => new FeatureWorkspaceChoice(row.Id, row.Name, row.Members, reasons.GetValueOrDefault(row.Id)))
            .ToArray();
    }

    private async Task AuthorizeAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required.");
        }
    }
}
