using ListingStudio.Application.Notifications;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Notifications;

public sealed class UserNotificationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IUserNotificationService
{
    public async Task<UserNotificationInbox> GetInboxAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var owned = dbContext.UserNotifications.AsNoTracking()
            .Where(notification => notification.UserId == userId
                && (notification.OrganizationId == organizationId
                    || notification.Kind == UserNotificationKind.TesterCampaignLimit));
        var rows = await owned
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .ThenByDescending(notification => notification.Id)
            .Take(IUserNotificationService.InboxSize)
            .ToListAsync(cancellationToken);
        var unread = await owned.CountAsync(notification => notification.ReadAtUtc == null, cancellationToken);
        var items = rows.Select(notification => new UserNotificationItem(
                notification.Id,
                notification.PropertyId,
                notification.Kind,
                notification.Outcome,
                notification.Title,
                notification.Message,
                UserNotificationItem.LinkFor(notification.PropertyId, notification.Tab, notification.Kind),
                notification.CreatedAtUtc,
                notification.IsRead))
            .ToArray();
        return new UserNotificationInbox(items, unread);
    }

    public async Task<bool> MarkReadAsync(
        string userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var notification = await dbContext.UserNotifications.SingleOrDefaultAsync(
            candidate => candidate.Id == notificationId
                && candidate.UserId == userId
                && (candidate.OrganizationId == organizationId
                    || candidate.Kind == UserNotificationKind.TesterCampaignLimit),
            cancellationToken);
        if (notification is null)
        {
            return false;
        }

        notification.MarkRead(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> MarkAllReadAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return await dbContext.UserNotifications
            .Where(notification => notification.UserId == userId
                && (notification.OrganizationId == organizationId
                    || notification.Kind == UserNotificationKind.TesterCampaignLimit)
                && notification.ReadAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.ReadAtUtc, now),
                cancellationToken);
    }

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }
}
