using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class AerialIntroClipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(75)]
    [InlineData(400)]
    public void OnlyTheTwoOfferedHeightsAreAccepted(int heightFeet)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(heightFeet));
    }

    [Fact]
    public void AnIntroRecordsTheAcknowledgementAndIsApprovedOnlyOnceFinished()
    {
        var clip = Create(AerialIntroClip.LowHeightFeet);
        Assert.Equal(AerialIntroClip.BriskPlaybackRate, clip.PlaybackRate);
        Assert.Equal(Now, clip.DisclosureAcknowledgedAtUtc);
        Assert.Equal("agent-1", clip.RequestedByUserId);
        Assert.True(clip.IsInProgress);
        Assert.Throws<InvalidOperationException>(() => clip.Approve("agent-1", Now));

        clip.BeginSubmission(Now);
        clip.MarkSubmitted("req-1", Now, Now.AddSeconds(10));
        clip.Complete("higgsfield", "kling", "intro.mp4", "video/mp4", 1_000, 5_000, Now.AddMinutes(2));
        Assert.False(clip.IsInProgress);
        Assert.False(clip.IsApproved);

        clip.Approve("agent-2", Now.AddMinutes(3));
        Assert.True(clip.IsApproved);
        Assert.Equal("agent-2", clip.ApprovedByUserId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void AnIntroAcceptsOnlyTheOfferedPlaybackRates(decimal playbackRate)
    {
        var clip = Create(AerialIntroClip.LowHeightFeet);

        clip.SetPlaybackRate(playbackRate);

        Assert.Equal(playbackRate, clip.PlaybackRate);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(2.5)]
    public void AnIntroRejectsAnUnsupportedPlaybackRate(decimal playbackRate)
    {
        var clip = Create(AerialIntroClip.LowHeightFeet);

        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SetPlaybackRate(playbackRate));
    }

    [Fact]
    public void AFailureRetriesUntilItsAttemptsRunOut()
    {
        var clip = Create(AerialIntroClip.HighHeightFeet);
        for (var attempt = 1; attempt <= AerialIntroClip.MaximumAttempts; attempt++)
        {
            clip.BeginSubmission(Now);
            clip.Fail("provider unavailable", Now.AddMinutes(1));
            Assert.Equal(attempt < AerialIntroClip.MaximumAttempts, clip.CanRetry);
        }

        Assert.Null(clip.NextCheckAtUtc);
        Assert.False(clip.IsInProgress);
        Assert.Throws<InvalidOperationException>(() => clip.BeginSubmission(Now));
    }

    private static AerialIntroClip Create(int heightFeet) => AerialIntroClip.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "organizations/a/properties/b/front.jpg",
        heightFeet,
        "higgsfield/v1/aerial-abc",
        "agent-1",
        Now);
}
