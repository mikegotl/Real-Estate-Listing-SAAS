using ListingStudio.Application.Authentication;
using ListingStudio.Application.Notifications;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyVideoProcessingTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] SampleUpload =
        [0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0];

    [Fact]
    public async Task OwnerCanUploadProcessReadAndDeleteEnhancedVideo()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "video-enhancement-owner");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "video-enhancement-outsider");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        await using var upload = new MemoryStream(SampleUpload);

        var videoId = await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("walkthrough.mp4", "video/mp4", upload.Length, upload));

        var queued = Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId));
        Assert.Equal(PropertyVideoProcessingStatus.Pending, queued.ProcessingStatus);
        Assert.Empty(await service.ListAsync(outsider.UserId, owner.PropertyId));
        Assert.Null(await service.OpenReadAsync(outsider.UserId, videoId, enhanced: false));

        var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoProcessingProcessor>();
        PropertyVideoProcessingRunResult? result;
        do
        {
            result = await processor.ProcessNextAsync();
        }
        while (result is not null && result.VideoId != videoId);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        var completed = Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId));
        Assert.Equal(PropertyVideoProcessingStatus.Completed, completed.ProcessingStatus);
        Assert.Equal(30m, completed.EnhancedFrameRate);
        Assert.Equal("fake-stabilize-color-v1", completed.EnhancementVersion);
        var inbox = await NotificationInbox.ForAsync(fixture, owner.UserId);
        var ready = Assert.Single(inbox.Items);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal((UserNotificationKind.VideoEnhancement, UserNotificationOutcome.Succeeded), (ready.Kind, ready.Outcome));
        Assert.Equal("Video ready", ready.Title);
        Assert.Equal("\"walkthrough.mp4\" for 123 Main Street is stabilized and color corrected.", ready.Message);
        Assert.Equal($"/properties/{owner.PropertyId}?tab=media", ready.Link);
        Assert.Empty((await NotificationInbox.ForAsync(fixture, outsider.UserId)).Items);
        var notifications = scope.ServiceProvider.GetRequiredService<IUserNotificationService>();
        Assert.False(await notifications.MarkReadAsync(outsider.UserId, ready.Id));
        Assert.Equal(0, await notifications.MarkAllReadAsync(outsider.UserId));
        Assert.Equal(1, (await NotificationInbox.ForAsync(fixture, owner.UserId)).UnreadCount);
        Assert.True(await notifications.MarkReadAsync(owner.UserId, ready.Id));
        var read = await NotificationInbox.ForAsync(fixture, owner.UserId);
        Assert.Equal(0, read.UnreadCount);
        Assert.True(Assert.Single(read.Items).IsRead);
        var enhanced = await service.OpenReadAsync(owner.UserId, videoId, enhanced: true);
        Assert.NotNull(enhanced);
        await using (enhanced.Content)
        {
            Assert.Equal("video/mp4", enhanced.MimeType);
            Assert.EndsWith("-enhanced.mp4", enhanced.Filename, StringComparison.Ordinal);
            Assert.True(enhanced.Content.Length > 0);
        }

        Assert.False(await service.DeleteAsync(outsider.UserId, owner.PropertyId, videoId));
        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, videoId));
        Assert.Empty(await service.ListAsync(owner.UserId, owner.PropertyId));
    }

    [Fact]
    public async Task FailedEnhancementIsRetryable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "video-retry-owner");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        await using var upload = new MemoryStream(SampleUpload);
        var videoId = await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("retry.mp4", "video/mp4", upload.Length, upload));
        fixture.Factory.PropertyVideoTranscoder.EnqueueFailure(new InvalidOperationException("test failure"));

        var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoProcessingProcessor>();
        var failed = await processor.ProcessNextAsync();

        Assert.NotNull(failed);
        Assert.False(failed.Succeeded);
        Assert.True(failed.WillRetry);
        Assert.Empty((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        var item = Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId));
        Assert.Equal(PropertyVideoProcessingStatus.Failed, item.ProcessingStatus);
        Assert.True(await service.RetryProcessingAsync(owner.UserId, owner.PropertyId, videoId));
        Assert.Equal(
            PropertyVideoProcessingStatus.Pending,
            Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId)).ProcessingStatus);
        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, videoId));
    }

    [Fact]
    public async Task OwnerCanLabelAndReorderWalkthroughWhileOtherTenantsCannot()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "video-tour-owner");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "video-tour-outsider");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        await using var firstUpload = new MemoryStream(SampleUpload);
        await using var secondUpload = new MemoryStream(SampleUpload);
        var exteriorId = await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("exterior.mp4", "video/mp4", firstUpload.Length, firstUpload));
        var kitchenId = await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("kitchen.mp4", "video/mp4", secondUpload.Length, secondUpload));

        Assert.False(await service.SetTourStopAsync(
            outsider.UserId, owner.PropertyId, exteriorId, PropertyMediaCategory.FrontExterior));
        Assert.False(await service.ReorderTourAsync(
            outsider.UserId, owner.PropertyId, [kitchenId, exteriorId]));
        Assert.True(await service.SetTourStopAsync(
            owner.UserId, owner.PropertyId, exteriorId, PropertyMediaCategory.FrontExterior));
        Assert.True(await service.SetTourStopAsync(
            owner.UserId, owner.PropertyId, kitchenId, PropertyMediaCategory.Kitchen));
        Assert.True(await service.ReorderTourAsync(
            owner.UserId, owner.PropertyId, [kitchenId, exteriorId]));

        var ordered = await service.ListAsync(owner.UserId, owner.PropertyId);
        Assert.Collection(
            ordered,
            item =>
            {
                Assert.Equal(kitchenId, item.Id);
                Assert.Equal(0, item.TourOrder);
                Assert.Equal(PropertyMediaCategory.Kitchen, item.TourStop);
            },
            item =>
            {
                Assert.Equal(exteriorId, item.Id);
                Assert.Equal(1, item.TourOrder);
                Assert.Equal(PropertyMediaCategory.FrontExterior, item.TourStop);
            });

        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, kitchenId));
        await using var replacementUpload = new MemoryStream(SampleUpload);
        var bedroomId = await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("bedroom.mp4", "video/mp4", replacementUpload.Length, replacementUpload));
        var compacted = await service.ListAsync(owner.UserId, owner.PropertyId);
        Assert.Equal([0, 1], compacted.Select(item => item.TourOrder));
        Assert.Equal([exteriorId, bedroomId], compacted.Select(item => item.Id));
        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, exteriorId));
        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, bedroomId));
    }

    private static async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var properties = services.GetRequiredService<IPropertyService>();
        var registrationResult = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));
        var propertyId = await properties.CreateAsync(registrationResult.UserId!, new PropertyInput(
            "123 Main Street",
            null,
            "Raleigh",
            "NC",
            "27601",
            450_000m,
            3,
            2.5m,
            2_100,
            0.25m,
            1998,
            PropertyType.SingleFamily,
            "A comfortable home.",
            ListingStatus.Draft));
        return (registrationResult.UserId!, propertyId);
    }
}
