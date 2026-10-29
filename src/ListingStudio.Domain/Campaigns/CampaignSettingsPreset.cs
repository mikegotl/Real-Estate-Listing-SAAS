using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Campaigns;

/// <summary>
/// A named set of Campaign tab choices saved by one member of a workspace. It is private to that member until they
/// share it, after which every member of the workspace can apply it.
/// </summary>
public sealed class CampaignSettingsPreset
{
    public const int MaximumNameLength = 60;

    public const int MaximumPresetsPerUser = 25;

    private CampaignSettingsPreset()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string OwnerUserId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The name compared for uniqueness: trimmed, single-spaced and upper-cased.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string SettingsJson { get; private set; } = string.Empty;

    public int SchemaVersion { get; private set; }

    public bool IsShared { get; private set; }

    /// <summary>The owner's choice for properties that have no campaign yet. At most one per owner.</summary>
    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public static CampaignSettingsPreset Create(
        Guid organizationId,
        string ownerUserId,
        string name,
        CampaignPresetSettings settings,
        DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A workspace is required.", nameof(organizationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        var preset = new CampaignSettingsPreset
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OwnerUserId = ownerUserId,
            CreatedAtUtc = createdAtUtc,
        };
        preset.Rename(name, createdAtUtc);
        preset.ReplaceSettings(settings, createdAtUtc);
        return preset;
    }

    public void Rename(string name, DateTimeOffset updatedAtUtc)
    {
        Name = CleanName(name);
        NormalizedName = NormalizeName(Name);
        UpdatedAtUtc = updatedAtUtc;
    }

    public void ReplaceSettings(CampaignPresetSettings settings, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = settings.Normalize().ToJson();
        if (json.Length > CampaignPresetSettings.MaximumJsonLength)
        {
            throw new ArgumentException("The saved settings are too large.", nameof(settings));
        }

        SettingsJson = json;
        SchemaVersion = CampaignPresetSettings.CurrentSchemaVersion;
        UpdatedAtUtc = updatedAtUtc;
    }

    public CampaignPresetSettingsRead ReadSettings() => CampaignPresetSettings.FromJson(SettingsJson);

    public void SetShared(bool shared, DateTimeOffset updatedAtUtc)
    {
        IsShared = shared;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetDefault(bool isDefault, DateTimeOffset updatedAtUtc)
    {
        IsDefault = isDefault;
        UpdatedAtUtc = updatedAtUtc;
    }

    public static string CleanName(string name)
    {
        var cleaned = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("Enter a name for these settings.", nameof(name));
        }

        if (cleaned.Length > MaximumNameLength)
        {
            throw new ArgumentException($"Use a name of up to {MaximumNameLength} characters.", nameof(name));
        }

        if (cleaned.Any(char.IsControl))
        {
            throw new ArgumentException("The name contains characters that are not allowed.", nameof(name));
        }

        return cleaned;
    }

    public static string NormalizeName(string name) => CleanName(name).ToUpperInvariant();
}
