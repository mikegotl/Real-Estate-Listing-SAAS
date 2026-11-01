using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

public enum VerticalMediaRoleSource
{
    /// <summary>Worked out from the photo analysis or video metadata.</summary>
    Suggested,

    /// <summary>Chosen by the agent; wins over any suggestion.</summary>
    Agent,
}

/// <summary>A role an item has, and whether the agent chose it.</summary>
public sealed record VerticalMediaRoleState(VerticalMediaRole Role, VerticalMediaRoleSource Source);

/// <summary>The agent's latest decision for one role on one item.</summary>
public sealed record VerticalMediaRoleOverride(
    Guid TargetId,
    VerticalMediaRole Role,
    VerticalMediaRoleDecision Decision,
    string SetByUserId,
    DateTimeOffset SetAtUtc);

/// <summary>What the suggester reads from one storyboard photo.</summary>
public sealed record VerticalRolePhoto(
    Guid MediaId,
    PropertyMediaCategory? Category,
    bool AnalysisCompleted,
    int? QualityScore,
    int? HeroScore,
    bool? IsExterior,
    int DisplayOrder);

/// <summary>What the suggester reads from one property video.</summary>
public sealed record VerticalRoleFootage(
    Guid VideoId,
    int DurationMs,
    bool IsReady,
    bool HasAudio,
    PropertyMediaCategory? TourStop);

/// <summary>
/// Suggests roles from analysis, then applies the agent's decisions. Deterministic. Roles that make a claim
/// (agent intro, before/after, live replay) are never suggested.
/// </summary>
public static class VerticalMediaRoles
{
    public const string SuggestionVersion = "media-roles-v1";

    private static readonly HashSet<PropertyMediaCategory> FeatureCategories =
    [
        PropertyMediaCategory.FrontExterior,
        PropertyMediaCategory.RearExterior,
        PropertyMediaCategory.Aerial,
        PropertyMediaCategory.Entry,
        PropertyMediaCategory.LivingRoom,
        PropertyMediaCategory.Kitchen,
        PropertyMediaCategory.DiningRoom,
        PropertyMediaCategory.PrimaryBedroom,
        PropertyMediaCategory.PrimaryBathroom,
        PropertyMediaCategory.Office,
        PropertyMediaCategory.Pool,
        PropertyMediaCategory.Patio,
        PropertyMediaCategory.Backyard,
        PropertyMediaCategory.Community,
    ];

    private static readonly HashSet<PropertyMediaCategory> ExteriorCategories =
    [
        PropertyMediaCategory.FrontExterior,
        PropertyMediaCategory.RearExterior,
        PropertyMediaCategory.Aerial,
    ];

    private static readonly HashSet<PropertyMediaCategory> LifestyleCategories =
    [
        PropertyMediaCategory.Pool,
        PropertyMediaCategory.Patio,
        PropertyMediaCategory.Backyard,
        PropertyMediaCategory.Community,
    ];

    private static readonly HashSet<PropertyMediaCategory> ClosingCategories =
    [
        PropertyMediaCategory.FrontExterior,
        PropertyMediaCategory.RearExterior,
        PropertyMediaCategory.Aerial,
        PropertyMediaCategory.Backyard,
        PropertyMediaCategory.Pool,
    ];

    public static bool IsFeatureCategory(PropertyMediaCategory? category) =>
        category is { } value && FeatureCategories.Contains(value);

    /// <summary>Suggested roles for every photo and video, keyed by its id.</summary>
    public static Dictionary<Guid, HashSet<VerticalMediaRole>> Suggest(
        IReadOnlyList<VerticalRolePhoto> photos,
        IReadOnlyList<VerticalRoleFootage> footage)
    {
        var result = new Dictionary<Guid, HashSet<VerticalMediaRole>>();
        HashSet<VerticalMediaRole> For(Guid id)
        {
            if (!result.TryGetValue(id, out var roles))
            {
                roles = [];
                result[id] = roles;
            }

            return roles;
        }

        var analyzed = photos
            .Where(photo => photo.AnalysisCompleted && photo.QualityScore >= 50)
            .OrderByDescending(photo => photo.HeroScore ?? 0)
            .ThenByDescending(photo => photo.QualityScore ?? 0)
            .ThenBy(photo => photo.DisplayOrder)
            .ThenBy(photo => photo.MediaId)
            .ToArray();
        var hero = analyzed.FirstOrDefault(photo =>
            photo.HeroScore >= 70 && photo.QualityScore >= 60 && IsFeatureCategory(photo.Category));
        if (hero is not null)
        {
            For(hero.MediaId).Add(VerticalMediaRole.HeroFeature);
        }

        foreach (var photo in analyzed)
        {
            if (photo != hero && photo.HeroScore >= 55 && photo.QualityScore >= 55 && IsFeatureCategory(photo.Category))
            {
                For(photo.MediaId).Add(VerticalMediaRole.FeatureDetail);
            }

            if (photo.IsExterior == true || photo.Category is { } category && ExteriorCategories.Contains(category))
            {
                For(photo.MediaId).Add(VerticalMediaRole.Exterior);
            }

            if (photo.Category is { } lifestyle && LifestyleCategories.Contains(lifestyle))
            {
                For(photo.MediaId).Add(VerticalMediaRole.Lifestyle);
            }
        }

        var closing = analyzed.FirstOrDefault(photo =>
            photo != hero && photo.QualityScore >= 60 && photo.Category is { } category && ClosingCategories.Contains(category))
            ?? analyzed.FirstOrDefault(photo =>
                photo.QualityScore >= 60 && photo.Category is { } category && ClosingCategories.Contains(category));
        if (closing is not null)
        {
            For(closing.MediaId).Add(VerticalMediaRole.ClosingView);
        }

        foreach (var video in footage.Where(video => video.IsReady))
        {
            // Spoken, continuous footage of at least 15 s is most likely a hosted walk-through; the agent confirms.
            if (video.HasAudio && video.DurationMs >= 15_000)
            {
                For(video.VideoId).Add(VerticalMediaRole.HostedWalkthrough);
            }

            if (video.TourStop is { } stop && ExteriorCategories.Contains(stop))
            {
                For(video.VideoId).Add(VerticalMediaRole.Exterior);
            }

            if (video.TourStop is { } outdoor && LifestyleCategories.Contains(outdoor))
            {
                For(video.VideoId).Add(VerticalMediaRole.Lifestyle);
            }
        }

        return result;
    }

    /// <summary>
    /// The roles in force for one item: suggestions, then the agent's latest decision per role. Assigned adds the role
    /// as the agent's, Removed drops it, Cleared goes back to the suggestion. A second Hero Feature chosen by the agent
    /// is allowed; the planner prefers the most recent.
    /// </summary>
    public static IReadOnlyList<VerticalMediaRoleState> Resolve(
        IReadOnlySet<VerticalMediaRole>? suggested,
        IEnumerable<VerticalMediaRoleOverride> overrides)
    {
        var states = new Dictionary<VerticalMediaRole, VerticalMediaRoleSource>();
        foreach (var role in suggested ?? new HashSet<VerticalMediaRole>())
        {
            if (!PropertyMediaRoleAssignment.AgentOnlyRoles.Contains(role))
            {
                states[role] = VerticalMediaRoleSource.Suggested;
            }
        }

        foreach (var latest in overrides
            .GroupBy(item => item.Role)
            .Select(group => group.OrderByDescending(item => item.SetAtUtc).First()))
        {
            switch (latest.Decision)
            {
                case VerticalMediaRoleDecision.Assigned:
                    states[latest.Role] = VerticalMediaRoleSource.Agent;
                    break;
                case VerticalMediaRoleDecision.Removed:
                    states.Remove(latest.Role);
                    break;
            }
        }

        return states
            .OrderBy(pair => pair.Key)
            .Select(pair => new VerticalMediaRoleState(pair.Key, pair.Value))
            .ToArray();
    }
}
