using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Notifications;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class UserNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LongTextIsShortenedAndReadTimeIsKeptFromTheFirstRead()
    {
        var notification = UserNotification.Create(
            Guid.NewGuid(),
            "user-1",
            Guid.NewGuid(),
            UserNotificationKind.VideoEnhancement,
            UserNotificationOutcome.Failed,
            "Video processing failed",
            new string('x', 2_000),
            JobNotifications.MediaTab,
            Now);

        Assert.Equal(UserNotification.MaximumMessageLength, notification.Message.Length);
        Assert.EndsWith("…", notification.Message, StringComparison.Ordinal);
        Assert.False(notification.IsRead);
        notification.MarkRead(Now.AddMinutes(1));
        notification.MarkRead(Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(1), notification.ReadAtUtc);
    }

    [Fact]
    public void RequiresAnOrganizationPropertyAndRecipient()
    {
        Assert.Throws<ArgumentException>(() => UserNotification.Create(
            Guid.Empty, "user-1", Guid.NewGuid(), UserNotificationKind.Campaign, UserNotificationOutcome.Succeeded,
            "Campaign ready", "Ready.", JobNotifications.CampaignTab, Now));
        Assert.Throws<ArgumentException>(() => UserNotification.Create(
            Guid.NewGuid(), " ", Guid.NewGuid(), UserNotificationKind.Campaign, UserNotificationOutcome.Succeeded,
            "Campaign ready", "Ready.", JobNotifications.CampaignTab, Now));
    }

    [Theory]
    [InlineData(3, 0, UserNotificationOutcome.Succeeded, "All 3 walk-through clips for 12 Oak St are ready.")]
    [InlineData(1, 0, UserNotificationOutcome.Succeeded, "The walk-through clip for 12 Oak St is ready.")]
    [InlineData(2, 1, UserNotificationOutcome.Succeeded, "2 of 3 walk-through clips for 12 Oak St are ready; 1 could not be made.")]
    [InlineData(0, 2, UserNotificationOutcome.Failed, "All 2 walk-through clips for 12 Oak St could not be made. You can try again on the Media tab.")]
    public void WalkthroughClipBatchesAreSummarizedInOneNote(
        int completed,
        int failed,
        UserNotificationOutcome outcome,
        string message)
    {
        var content = JobNotificationText.WalkthroughClipsFinished("12 Oak St", completed, failed);

        Assert.Equal(outcome, content.Outcome);
        Assert.Equal(message, content.Message);
        Assert.Equal(JobNotifications.MediaTab, content.Tab);
    }

    [Theory]
    [InlineData(3, 0, "All 3 fixed photos for 12 Oak St are in place on the Media tab.")]
    [InlineData(2, 1, "2 of 3 fixed photos for 12 Oak St are in place; 1 could not be fixed.")]
    [InlineData(0, 2, "2 photos for 12 Oak St could not be fixed. The photo editing service could not be reached.")]
    public void PhotoFixBatchesAreSummarizedInOneNote(int completed, int failed, string message) =>
        Assert.Equal(
            message,
            JobNotificationText.PhotoFixesFinished("12 Oak St", completed, failed, "The photo editing service could not be reached.").Message);

    [Fact]
    public void FailuresSayWhereTheWorkStoppedAndWhy()
    {
        var campaign = JobNotificationText.CampaignFailed(
            "12 Oak St",
            CampaignGenerationStage.GenerateNarration,
            "The voice provider request failed.");
        var video = JobNotificationText.VideoEnhancementFailed("12 Oak St", "tour.mp4", null);

        Assert.Equal(
            "The campaign for 12 Oak St stopped while recording the narration. You can resume it on the Campaign tab. The voice provider request failed.",
            campaign.Message);
        Assert.Equal(JobNotifications.CampaignTab, campaign.Tab);
        Assert.Equal("\"tour.mp4\" for 12 Oak St could not be processed.", video.Message);
        Assert.Equal(
            "No new room photos were found in \"tour.mp4\" for 12 Oak St.",
            JobNotificationText.RoomPhotosFound("12 Oak St", "tour.mp4", 0).Message);
    }
}
