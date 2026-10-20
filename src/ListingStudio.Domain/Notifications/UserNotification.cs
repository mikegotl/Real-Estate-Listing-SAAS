using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Notifications;

/// <summary>The long-running operation a notification reports on.</summary>
public enum UserNotificationKind
{
    VideoEnhancement,
    RoomPhotos,
    WalkthroughClips,
    Campaign,
    PhotoFixes,
    Lead,
}

public enum UserNotificationOutcome
{
    Succeeded,
    Failed,
}

/// <summary>
/// Tells one member of an organization that a background operation on one of its properties has finished or failed
/// for good. It is stored, so it is still waiting after the member signs out and back in.
/// </summary>
public sealed class UserNotification
{
    public const int MaximumTitleLength = 120;
    public const int MaximumMessageLength = 600;
    public const int MaximumTabLength = 32;

    private UserNotification()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public Guid PropertyId { get; private set; }
    public UserNotificationKind Kind { get; private set; }
    public UserNotificationOutcome Outcome { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;

    /// <summary>The property details tab the notification opens, such as "media" or "campaign".</summary>
    public string Tab { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ReadAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;

    public bool IsRead => ReadAtUtc.HasValue;

    public static UserNotification Create(
        Guid organizationId,
        string userId,
        Guid propertyId,
        UserNotificationKind kind,
        UserNotificationOutcome outcome,
        string title,
        string message,
        string tab,
        DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("An organization and property are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The notification kind or outcome is not supported.");
        }

        return new UserNotification
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = userId.Trim(),
            PropertyId = propertyId,
            Kind = kind,
            Outcome = outcome,
            Title = Bounded(title, MaximumTitleLength, nameof(title)),
            Message = Bounded(message, MaximumMessageLength, nameof(message)),
            Tab = Bounded(tab, MaximumTabLength, nameof(tab)),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void MarkRead(DateTimeOffset readAtUtc)
    {
        ReadAtUtc ??= readAtUtc;
    }

    // Text is built from filenames and provider errors, so it is shortened rather than rejected when it runs long.
    private static string Bounded(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }
}
