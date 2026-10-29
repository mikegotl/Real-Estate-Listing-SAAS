using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class CampaignSettingsPresetService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : ICampaignSettingsPresetService
{
    public async Task<CampaignSettingsPresetList> ListAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var membership = await GetMembershipAsync(userId, cancellationToken);
        var presets = await dbContext.CampaignSettingsPresets.AsNoTracking()
            .Where(preset => preset.OrganizationId == membership.OrganizationId
                && (preset.OwnerUserId == userId || preset.IsShared))
            .ToArrayAsync(cancellationToken);
        var otherOwnerIds = presets.Select(preset => preset.OwnerUserId).Where(id => id != userId).Distinct().ToArray();
        var ownerEmails = otherOwnerIds.Length == 0
            ? new Dictionary<string, string?>()
            : await dbContext.Users.AsNoTracking()
                .Where(user => otherOwnerIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);
        var hasOtherMembers = await dbContext.OrganizationMembers.AsNoTracking()
            .AnyAsync(
                member => member.OrganizationId == membership.OrganizationId && member.UserId != userId,
                cancellationToken);
        var items = new List<CampaignSettingsPresetItem>(presets.Length);
        foreach (var preset in presets
            .OrderBy(preset => preset.OwnerUserId == userId ? 0 : 1)
            .ThenBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // A set that cannot be read at all is left out rather than breaking the Campaign tab.
            if (TryToItem(preset, userId, membership.Role, ownerEmails.GetValueOrDefault(preset.OwnerUserId)) is { } item)
            {
                items.Add(item);
            }
        }

        return new CampaignSettingsPresetList(items, hasOtherMembers);
    }

    public async Task<CampaignSettingsPresetItem> SaveAsync(
        string userId,
        string name,
        CampaignPresetSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var membership = await GetMembershipAsync(userId, cancellationToken);
        var count = await dbContext.CampaignSettingsPresets.CountAsync(
            preset => preset.OrganizationId == membership.OrganizationId && preset.OwnerUserId == userId,
            cancellationToken);
        if (count >= CampaignSettingsPreset.MaximumPresetsPerUser)
        {
            throw new InvalidOperationException(
                $"You can keep up to {CampaignSettingsPreset.MaximumPresetsPerUser} saved settings. Delete one to save another.");
        }

        var preset = CampaignSettingsPreset.Create(
            membership.OrganizationId,
            userId,
            name,
            settings,
            timeProvider.GetUtcNow());
        await EnsureNameAvailableAsync(preset, cancellationToken);
        dbContext.CampaignSettingsPresets.Add(preset);
        try
        {
            await SaveNamedAsync(cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        return ToItem(preset, userId, membership.Role, null);
    }

    public async Task<CampaignSettingsPresetItem?> UpdateSettingsAsync(
        string userId,
        Guid presetId,
        CampaignPresetSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return await MutateAsync(userId, presetId, ownOnly: false, (preset, now) =>
        {
            preset.ReplaceSettings(settings, now);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<CampaignSettingsPresetItem?> RenameAsync(
        string userId,
        Guid presetId,
        string name,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(userId, presetId, ownOnly: false, async (preset, now) =>
        {
            preset.Rename(name, now);
            await EnsureNameAvailableAsync(preset, cancellationToken);
        }, cancellationToken);

    public async Task<CampaignSettingsPresetItem?> SetSharedAsync(
        string userId,
        Guid presetId,
        bool isShared,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(userId, presetId, ownOnly: false, (preset, now) =>
        {
            preset.SetShared(isShared, now);
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<CampaignSettingsPresetItem?> SetDefaultAsync(
        string userId,
        Guid presetId,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var membership = await GetMembershipAsync(userId, cancellationToken);
        var preset = await FindManageableAsync(membership, userId, presetId, ownOnly: true, cancellationToken);
        if (preset is null)
        {
            return null;
        }

        try
        {
            // The one-default index is checked per statement, so the old default is cleared before the new one is set.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (isDefault)
            {
                var previous = await dbContext.CampaignSettingsPresets
                    .Where(candidate => candidate.OrganizationId == membership.OrganizationId
                        && candidate.OwnerUserId == userId
                        && candidate.IsDefault
                        && candidate.Id != presetId)
                    .ToArrayAsync(cancellationToken);
                foreach (var candidate in previous)
                {
                    candidate.SetDefault(false, now);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            preset.SetDefault(isDefault, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }

        return ToItem(preset, userId, membership.Role, null);
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid presetId,
        CancellationToken cancellationToken = default)
    {
        var membership = await GetMembershipAsync(userId, cancellationToken);
        var preset = await FindManageableAsync(membership, userId, presetId, ownOnly: false, cancellationToken);
        if (preset is null)
        {
            return false;
        }

        dbContext.CampaignSettingsPresets.Remove(preset);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<CampaignSettingsPresetItem?> MutateAsync(
        string userId,
        Guid presetId,
        bool ownOnly,
        Func<CampaignSettingsPreset, DateTimeOffset, Task> change,
        CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(userId, cancellationToken);
        var preset = await FindManageableAsync(membership, userId, presetId, ownOnly, cancellationToken);
        if (preset is null)
        {
            return null;
        }

        try
        {
            await change(preset, timeProvider.GetUtcNow());
            await SaveNamedAsync(cancellationToken);
        }
        catch
        {
            // The Campaign tab keeps one context for the whole visit; a refused change must not ride along with the next save.
            dbContext.ChangeTracker.Clear();
            throw;
        }

        var sharedBy = preset.OwnerUserId == userId
            ? null
            : await dbContext.Users.AsNoTracking()
                .Where(user => user.Id == preset.OwnerUserId)
                .Select(user => user.Email)
                .SingleOrDefaultAsync(cancellationToken);
        return ToItem(preset, userId, membership.Role, sharedBy);
    }

    /// <summary>
    /// The creator can change their own sets. A workspace owner can also change sets others have shared, so a stale
    /// shared set never needs its creator to tidy it up.
    /// </summary>
    private async Task<CampaignSettingsPreset?> FindManageableAsync(
        Membership membership,
        string userId,
        Guid presetId,
        bool ownOnly,
        CancellationToken cancellationToken)
    {
        // Another member may have changed the set since this context last loaded it, so read it fresh.
        dbContext.ChangeTracker.Clear();
        var preset = await dbContext.CampaignSettingsPresets.SingleOrDefaultAsync(
            candidate => candidate.Id == presetId && candidate.OrganizationId == membership.OrganizationId,
            cancellationToken);
        return preset is not null && CanManage(preset, userId, membership.Role, ownOnly) ? preset : null;
    }

    private static bool CanManage(
        CampaignSettingsPreset preset,
        string userId,
        OrganizationMemberRole role,
        bool ownOnly = false) =>
        preset.OwnerUserId == userId
        || (!ownOnly && preset.IsShared && role == OrganizationMemberRole.Owner);

    private async Task EnsureNameAvailableAsync(CampaignSettingsPreset preset, CancellationToken cancellationToken)
    {
        var taken = await dbContext.CampaignSettingsPresets.AnyAsync(
            candidate => candidate.OrganizationId == preset.OrganizationId
                && candidate.OwnerUserId == preset.OwnerUserId
                && candidate.NormalizedName == preset.NormalizedName
                && candidate.Id != preset.Id,
            cancellationToken);
        if (taken)
        {
            throw new InvalidOperationException($"You already have saved settings named \"{preset.Name}\".");
        }
    }

    private async Task SaveNamedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Two tabs saving the same name at once: the unique index wins, and the user sees the same message.
            throw new InvalidOperationException("You already have saved settings with that name.", exception);
        }
    }

    private async Task<Membership> GetMembershipAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => new Membership(member.OrganizationId, member.Role))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static CampaignSettingsPresetItem? TryToItem(
        CampaignSettingsPreset preset,
        string userId,
        OrganizationMemberRole role,
        string? ownerEmail)
    {
        try
        {
            return ToItem(preset, userId, role, ownerEmail);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static CampaignSettingsPresetItem ToItem(
        CampaignSettingsPreset preset,
        string userId,
        OrganizationMemberRole role,
        string? ownerEmail)
    {
        var read = preset.ReadSettings();
        var isOwn = preset.OwnerUserId == userId;
        return new CampaignSettingsPresetItem(
            preset.Id,
            preset.Name,
            read.Settings,
            read.SkippedValues,
            isOwn,
            preset.IsShared,
            isOwn && preset.IsDefault,
            CanManage(preset, userId, role),
            isOwn ? null : ownerEmail ?? "another member",
            preset.UpdatedAtUtc);
    }

    private sealed record Membership(Guid OrganizationId, OrganizationMemberRole Role);
}
