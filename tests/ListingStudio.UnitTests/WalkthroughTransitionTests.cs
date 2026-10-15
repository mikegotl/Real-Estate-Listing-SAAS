using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class WalkthroughTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MovesFromSubmissionThroughGenerationToCompletion()
    {
        var transition = Create();

        transition.BeginSubmission(Now);
        transition.MarkSubmitted("request-1", Now, Now.AddSeconds(10));
        transition.ScheduleCheck(Now.AddSeconds(20));
        transition.Complete("higgsfield", "kling", "clips/a.mp4", "video/mp4", 1_024, 5_000, Now.AddMinutes(1));

        Assert.Equal(WalkthroughTransitionStatus.Completed, transition.Status);
        Assert.Equal(1, transition.AttemptCount);
        Assert.Equal("request-1", transition.ProviderRequestId);
        Assert.Null(transition.NextCheckAtUtc);
        Assert.Equal(5_000, transition.DurationMs);
    }

    [Fact]
    public void StopsRetryingAfterTheLastAttemptUntilRequeued()
    {
        var transition = Create();
        for (var attempt = 1; attempt <= WalkthroughTransition.MaximumAttempts; attempt++)
        {
            transition.BeginSubmission(Now);
            transition.Fail(new string('x', 600), Now.AddMinutes(attempt));
        }

        Assert.False(transition.CanRetry);
        Assert.Null(transition.NextCheckAtUtc);
        Assert.Equal(WalkthroughTransition.MaximumErrorLength, transition.LastError!.Length);
        Assert.Throws<InvalidOperationException>(() => transition.BeginSubmission(Now));

        transition.Requeue();

        Assert.Equal(WalkthroughTransitionStatus.Pending, transition.Status);
        Assert.Equal(0, transition.AttemptCount);
        transition.BeginSubmission(Now);
        Assert.Equal(WalkthroughTransitionStatus.Submitting, transition.Status);
    }

    [Fact]
    public void RejectsAPairWithTheSameShotTwice()
    {
        var shot = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => WalkthroughTransition.Create(
            Guid.NewGuid(), Guid.NewGuid(), shot, shot, "v1", Now));
    }

    [Fact]
    public void CannotCompleteBeforeTheProviderAcceptsTheRequest()
    {
        var transition = Create();
        transition.BeginSubmission(Now);

        Assert.Throws<InvalidOperationException>(() => transition.Complete(
            "higgsfield", "kling", "clips/a.mp4", "video/mp4", 1_024, 5_000, Now));
    }

    private static WalkthroughTransition Create() => WalkthroughTransition.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "higgsfield/v1", Now);
}
