using ListingStudio.Domain.Notifications;

namespace ListingStudio.Application.Notifications;

/// <summary>The signed-in member's own notifications about finished background work.</summary>
public interface IUserNotificationService
{
    public const int InboxSize = 20;

    /// <summary>The newest notifications, newest first, and how many of all the member's notifications are unread.</summary>
    Task<UserNotificationInbox> GetInboxAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> MarkReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default);

    Task<int> MarkAllReadAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed record UserNotificationInbox(IReadOnlyList<UserNotificationItem> Items, int UnreadCount);

public sealed record UserNotificationItem(
    Guid Id,
    Guid PropertyId,
    UserNotificationKind Kind,
    UserNotificationOutcome Outcome,
    string Title,
    string Message,
    string Link,
    DateTimeOffset CreatedAtUtc,
    bool IsRead)
{
    public static string LinkFor(Guid propertyId, string tab, UserNotificationKind kind) =>
        kind switch
        {
            UserNotificationKind.Lead => $"/leads?propertyId={propertyId}",
            UserNotificationKind.TesterCampaignLimit => "/admin/users?testers=true",
            _ => $"/properties/{propertyId}?tab={Uri.EscapeDataString(tab)}",
        };
}
