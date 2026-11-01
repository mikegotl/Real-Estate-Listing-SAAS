namespace ListingStudio.Domain.Properties;

/// <summary>
/// What a photo or property video is good for in a vertical video. Suggested from the media analysis or chosen by the
/// agent; the agent's choice always wins. Values are stored by name; append new ones.
/// </summary>
public enum VerticalMediaRole
{
    /// <summary>The single most striking feature; opens a Wow Feature video.</summary>
    HeroFeature,

    /// <summary>A close or secondary view of a feature.</summary>
    FeatureDetail,

    /// <summary>The agent introducing the listing on camera. Agent only.</summary>
    AgentIntro,

    /// <summary>A walk-through filmed from the host's point of view.</summary>
    HostedWalkthrough,

    /// <summary>The property before work the agent confirmed. Agent only.</summary>
    BeforeRenovation,

    /// <summary>The property after work the agent confirmed. Agent only.</summary>
    AfterRenovation,

    Exterior,

    /// <summary>Outdoor living, amenities or community spaces.</summary>
    Lifestyle,

    /// <summary>A recording of a live tour or open house. Agent only.</summary>
    LiveReplay,

    /// <summary>A strong final shot to end on.</summary>
    ClosingView,
}

/// <summary>What the agent decided about one role on one photo or video.</summary>
public enum VerticalMediaRoleDecision
{
    /// <summary>The item has the role, whatever the analysis suggests.</summary>
    Assigned,

    /// <summary>The item does not have the role, whatever the analysis suggests.</summary>
    Removed,

    /// <summary>The agent went back to the suggestion.</summary>
    Cleared,
}

/// <summary>
/// One agent decision about one role on one photo or property video. Rows are append-only: the latest row for an item
/// and role is in force, and earlier rows stay as the audit trail of who changed what and when.
/// </summary>
public sealed class PropertyMediaRoleAssignment
{
    private PropertyMediaRoleAssignment() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid? PropertyMediaId { get; private set; }

    public Guid? PropertyVideoId { get; private set; }

    public VerticalMediaRole Role { get; private set; }

    public VerticalMediaRoleDecision Decision { get; private set; }

    public string SetByUserId { get; private set; } = string.Empty;

    public DateTimeOffset SetAtUtc { get; private set; }

    /// <summary>Roles only an agent can give: claims about the agent, a renovation or a live event.</summary>
    public static IReadOnlySet<VerticalMediaRole> AgentOnlyRoles { get; } = new HashSet<VerticalMediaRole>
    {
        VerticalMediaRole.AgentIntro,
        VerticalMediaRole.BeforeRenovation,
        VerticalMediaRole.AfterRenovation,
        VerticalMediaRole.LiveReplay,
    };

    /// <summary>Roles that describe moving footage, so they cannot be given to a photo.</summary>
    public static IReadOnlySet<VerticalMediaRole> FootageOnlyRoles { get; } = new HashSet<VerticalMediaRole>
    {
        VerticalMediaRole.AgentIntro,
        VerticalMediaRole.HostedWalkthrough,
        VerticalMediaRole.LiveReplay,
    };

    public Guid TargetId => PropertyMediaId ?? PropertyVideoId ?? Guid.Empty;

    public static PropertyMediaRoleAssignment Create(
        Guid organizationId,
        Guid propertyId,
        Guid? propertyMediaId,
        Guid? propertyVideoId,
        VerticalMediaRole role,
        VerticalMediaRoleDecision decision,
        string userId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("A workspace and property are required.");
        }

        if (propertyMediaId.HasValue == propertyVideoId.HasValue
            || propertyMediaId == Guid.Empty
            || propertyVideoId == Guid.Empty)
        {
            throw new ArgumentException("Choose exactly one photo or video.");
        }

        if (!Enum.IsDefined(role) || !Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (propertyMediaId.HasValue && FootageOnlyRoles.Contains(role))
        {
            throw new ArgumentException("That role is only for property videos.", nameof(role));
        }

        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
        {
            throw new ArgumentException("The user identity is invalid.", nameof(userId));
        }

        return new PropertyMediaRoleAssignment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            PropertyMediaId = propertyMediaId,
            PropertyVideoId = propertyVideoId,
            Role = role,
            Decision = decision,
            SetByUserId = userId,
            SetAtUtc = now,
        };
    }
}
