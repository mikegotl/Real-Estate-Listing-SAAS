namespace ListingStudio.Application.Administration;

/// <summary>
/// The one place that decides whether a release unit is on. A feature with no saved switch is off. The global kill
/// switch overrides the pilot list. Answers are cached for <see cref="CacheLifetime"/> in each process, so turning a
/// feature off reaches the Web and Worker hosts within that time, with no deployment.
/// </summary>
public interface IFeatureStateService
{
    static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);

    Task<bool> IsEnabledAsync(string featureKey, Guid organizationId, CancellationToken cancellationToken = default);

    Task<FeatureFlagState> GetAsync(string featureKey, CancellationToken cancellationToken = default);

    /// <summary>Sets a feature's switch and pilot list. Platform administrators only.</summary>
    Task<FeatureFlagState> ConfigureAsync(
        string userId,
        string featureKey,
        bool globallyEnabled,
        bool enabledForAllOrganizations,
        IReadOnlyCollection<Guid> pilotOrganizationIds,
        CancellationToken cancellationToken = default);
}

public sealed record FeatureFlagState(
    string Key,
    bool IsGloballyEnabled,
    bool IsEnabledForAllOrganizations,
    IReadOnlyList<Guid> PilotOrganizationIds,
    DateTimeOffset? UpdatedAtUtc)
{
    public bool IsEnabledFor(Guid organizationId) =>
        IsGloballyEnabled && (IsEnabledForAllOrganizations || PilotOrganizationIds.Contains(organizationId));

    public static FeatureFlagState Off(string key) => new(key, false, false, [], null);
}
