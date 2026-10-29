using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Named Campaign tab settings. Each set belongs to the member who saved it; shared sets can be applied by everyone in
/// the workspace and managed by their creator or a workspace owner. Mutations return null when the set does not exist
/// or the user may not change it.
/// </summary>
public interface ICampaignSettingsPresetService
{
    Task<CampaignSettingsPresetList> ListAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<CampaignSettingsPresetItem> SaveAsync(
        string userId,
        string name,
        CampaignPresetSettings settings,
        CancellationToken cancellationToken = default);

    Task<CampaignSettingsPresetItem?> UpdateSettingsAsync(
        string userId,
        Guid presetId,
        CampaignPresetSettings settings,
        CancellationToken cancellationToken = default);

    Task<CampaignSettingsPresetItem?> RenameAsync(
        string userId,
        Guid presetId,
        string name,
        CancellationToken cancellationToken = default);

    Task<CampaignSettingsPresetItem?> SetSharedAsync(
        string userId,
        Guid presetId,
        bool isShared,
        CancellationToken cancellationToken = default);

    /// <summary>Makes one of the user's own sets (or none) the starting point for properties with no campaign.</summary>
    Task<CampaignSettingsPresetItem?> SetDefaultAsync(
        string userId,
        Guid presetId,
        bool isDefault,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        Guid presetId,
        CancellationToken cancellationToken = default);
}

/// <param name="WorkspaceHasOtherMembers">Sharing is offered only when someone else could see the set.</param>
public sealed record CampaignSettingsPresetList(
    IReadOnlyList<CampaignSettingsPresetItem> Items,
    bool WorkspaceHasOtherMembers)
{
    public CampaignSettingsPresetItem? Default => Items.FirstOrDefault(item => item.IsOwn && item.IsDefault);
}

/// <param name="SkippedValues">Saved choices this version of the app no longer offers.</param>
/// <param name="SharedBy">The creator's email when the set is someone else's shared set.</param>
public sealed record CampaignSettingsPresetItem(
    Guid Id,
    string Name,
    CampaignPresetSettings Settings,
    IReadOnlyList<string> SkippedValues,
    bool IsOwn,
    bool IsShared,
    bool IsDefault,
    bool CanManage,
    string? SharedBy,
    DateTimeOffset UpdatedAtUtc);
