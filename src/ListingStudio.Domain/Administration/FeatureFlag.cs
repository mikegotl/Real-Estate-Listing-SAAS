namespace ListingStudio.Domain.Administration;

/// <summary>The release units the platform can switch on and off without a deployment.</summary>
public static class FeatureFlagKeys
{
    /// <summary>
    /// Recommended multi-format vertical videos (the vertical video library). Off restores the single shared
    /// vertical video everywhere.
    /// </summary>
    public const string VerticalVideoLibraryV1 = "VerticalVideoLibraryV1";

    /// <summary>
    /// Experimental 30-second Kling 4 tour assembled from the approved Tour Map. Off keeps the established
    /// scene-by-scene campaign renderer as the control.
    /// </summary>
    public const string Kling4FullTourV1 = "Kling4FullTourV1";

    public static IReadOnlyList<string> All { get; } = [VerticalVideoLibraryV1, Kling4FullTourV1];

    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);
}

/// <summary>
/// One release unit's switch. <see cref="IsGloballyEnabled"/> is the kill switch: when it is off the feature is off
/// for every workspace, whatever the pilot list says. When it is on, the feature reaches every workspace if
/// <see cref="IsEnabledForAllOrganizations"/> is set, otherwise only the workspaces on the pilot list.
/// </summary>
public sealed class FeatureFlag
{
    private FeatureFlag() { }

    public string Key { get; private set; } = string.Empty;

    public bool IsGloballyEnabled { get; private set; }

    public bool IsEnabledForAllOrganizations { get; private set; }

    public string? UpdatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public List<FeatureFlagOrganization> Organizations { get; private set; } = [];

    public static FeatureFlag Create(string key, DateTimeOffset now)
    {
        if (!FeatureFlagKeys.IsKnown(key))
        {
            throw new ArgumentException("Unknown feature.", nameof(key));
        }

        return new FeatureFlag { Key = key, UpdatedAtUtc = now };
    }

    public void Configure(bool globallyEnabled, bool enabledForAllOrganizations, string? userId, DateTimeOffset now)
    {
        if (userId is { Length: > 450 })
        {
            throw new ArgumentException("The user identity is invalid.", nameof(userId));
        }

        IsGloballyEnabled = globallyEnabled;
        IsEnabledForAllOrganizations = enabledForAllOrganizations;
        UpdatedByUserId = userId;
        UpdatedAtUtc = now;
    }

    /// <summary>Whether the feature is on for one workspace: the kill switch always wins over the pilot list.</summary>
    public bool IsEnabledFor(Guid organizationId) =>
        IsGloballyEnabled
        && (IsEnabledForAllOrganizations || Organizations.Any(item => item.OrganizationId == organizationId));
}

/// <summary>A workspace on a feature's pilot list.</summary>
public sealed class FeatureFlagOrganization
{
    private FeatureFlagOrganization() { }

    public string FeatureFlagKey { get; private set; } = string.Empty;

    public Guid OrganizationId { get; private set; }

    public DateTimeOffset AddedAtUtc { get; private set; }

    public static FeatureFlagOrganization Create(string key, Guid organizationId, DateTimeOffset now)
    {
        if (!FeatureFlagKeys.IsKnown(key) || organizationId == Guid.Empty)
        {
            throw new ArgumentException("A known feature and a workspace are required.");
        }

        return new FeatureFlagOrganization { FeatureFlagKey = key, OrganizationId = organizationId, AddedAtUtc = now };
    }
}
