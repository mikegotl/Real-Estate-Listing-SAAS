using ListingStudio.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.IntegrationTests;

internal static class NotificationInbox
{
    public static async Task<UserNotificationInbox> ForAsync(PostgreSqlWebApplicationFixture fixture, string userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserNotificationService>().GetInboxAsync(userId);
    }
}
