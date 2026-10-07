using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
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
        var item = Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId));
        Assert.Equal(PropertyVideoProcessingStatus.Failed, item.ProcessingStatus);
        Assert.True(await service.RetryProcessingAsync(owner.UserId, owner.PropertyId, videoId));
        Assert.Equal(
            PropertyVideoProcessingStatus.Pending,
            Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId)).ProcessingStatus);
        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, videoId));
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
