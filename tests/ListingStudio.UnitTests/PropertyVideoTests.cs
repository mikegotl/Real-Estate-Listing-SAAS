using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyVideoTests
{
    [Fact]
    public void ProcessingLifecycleRecordsEnhancedAssetMetadata()
    {
        var video = CreateVideo();
        var started = DateTimeOffset.UtcNow;

        video.BeginProcessing(started);
        video.CompleteProcessing(
            "organizations/org/properties/property/videos/video/enhanced.mp4",
            2_048,
            1_920,
            1_080,
            14_900,
            30m,
            "ffmpeg-stabilize-color-v1",
            started.AddMinutes(1));

        Assert.Equal(PropertyVideoProcessingStatus.Completed, video.ProcessingStatus);
        Assert.Equal(1, video.ProcessingAttemptCount);
        Assert.Equal(30m, video.EnhancedFrameRate);
        Assert.Equal("ffmpeg-stabilize-color-v1", video.EnhancementVersion);
        Assert.Null(video.ProcessingLastError);
    }

    [Fact]
    public void FailedProcessingCanBeExplicitlyRequeued()
    {
        var video = CreateVideo();
        video.BeginProcessing(DateTimeOffset.UtcNow);
        video.FailProcessing("FFmpeg failed.", DateTimeOffset.UtcNow.AddMinutes(1));

        video.QueueRetry();

        Assert.Equal(PropertyVideoProcessingStatus.Pending, video.ProcessingStatus);
        Assert.Equal(0, video.ProcessingAttemptCount);
        Assert.Null(video.ProcessingLastError);
    }

    [Fact]
    public void TourOrderAndVerifiedStopCanBeEdited()
    {
        var video = CreateVideo();

        video.SetTourOrder(3);
        video.SetTourStop(PropertyMediaCategory.Kitchen);

        Assert.Equal(3, video.TourOrder);
        Assert.Equal(PropertyMediaCategory.Kitchen, video.TourStop);

        video.SetTourStop(null);
        Assert.Null(video.TourStop);
        Assert.Throws<ArgumentOutOfRangeException>(() => video.SetTourOrder(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => video.SetTourOrder(10));
    }

    private static PropertyVideo CreateVideo() => PropertyVideo.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "organizations/org/properties/property/videos/video/original.mp4",
        "walkthrough.mp4",
        "video/mp4",
        1_024,
        1_920,
        1_080,
        15_000,
        29.97m,
        true);
}
