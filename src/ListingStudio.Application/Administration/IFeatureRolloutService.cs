namespace ListingStudio.Application.Administration;

/// <summary>
/// The admin page for release switches (/admin/features). Platform administrators only. Every change goes through the
/// central <see cref="IFeatureStateService"/>, so the global kill switch keeps precedence over the pilot list, and
/// every change is written to the platform admin audit log.
/// </summary>
public interface IFeatureRolloutService
{
    Task<FeatureRolloutView> GetAsync(string userId, string featureKey, CancellationToken cancellationToken = default);

    Task<FeatureRolloutView> SaveAsync(string userId, string featureKey, bool globallyEnabled, bool enabledForAllOrganizations,
        IReadOnlyCollection<Guid> pilotOrganizationIds, CancellationToken cancellationToken = default);

    /// <summary>Workspaces whose name contains the text, for adding to the pilot list (at most 20).</summary>
    Task<IReadOnlyList<FeatureWorkspaceChoice>> SearchWorkspacesAsync(string userId, string query, CancellationToken cancellationToken = default);
}

/// <summary>A workspace as the rollout page shows it. <see cref="Reason"/> says why it's suggested, if it is.</summary>
public sealed record FeatureWorkspaceChoice(Guid Id, string Name, int Members, string? Reason);

public sealed record FeatureRolloutView(
    FeatureFlagState State,
    IReadOnlyList<FeatureWorkspaceChoice> Pilots,
    IReadOnlyList<FeatureWorkspaceChoice> SuggestedPilots)
{
    /// <summary>Who has the feature right now, in one sentence.</summary>
    public string Summary => FeatureRolloutText.Summary(State.IsGloballyEnabled, State.IsEnabledForAllOrganizations, Pilots.Count);
}

public static class FeatureRolloutText
{
    public const string AuditAction = "platform-admin.features.configure";

    public static string Summary(bool global, bool everyone, int pilots) => (global, everyone, pilots) switch
    {
        (false, _, _) => "Off for everyone. The global switch is off, so the pilot list is ignored.",
        (true, true, _) => "On for every workspace.",
        (true, false, 0) => "On, but no workspace is in the pilot list yet, so nobody has it.",
        (true, false, 1) => "On for 1 pilot workspace.",
        _ => $"On for {pilots} pilot workspaces.",
    };

    /// <summary>The audit outcome: switch positions and the pilot count only, never workspace names.</summary>
    public static string AuditOutcome(bool global, bool everyone, int pilots) =>
        $"global={(global ? 1 : 0)} all={(everyone ? 1 : 0)} pilots={Math.Min(pilots, 99_999)}";
}
