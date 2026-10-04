using ListingStudio.Domain.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignGenerationJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletedStagesAdvanceDurablyAndResetAttemptState()
    {
        var job = CreateJob();

        job.BeginStage(Now, TimeSpan.FromMinutes(10));
        job.CompleteStage(CampaignGenerationStage.AnalyzeMedia, Now.AddSeconds(1));

        Assert.Equal(CampaignGenerationStatus.Queued, job.Status);
        Assert.Equal(CampaignGenerationStage.AnalyzeMedia, job.CurrentStage);
        Assert.Equal(0, job.StageAttemptCount);
        Assert.Equal(1, job.TotalStageAttempts);
        Assert.Equal(Now.AddSeconds(1), job.NextAttemptAtUtc);
        Assert.Null(job.LeaseExpiresAtUtc);
    }

    [Fact]
    public void FailedStageCanResumeWithoutLosingItsCheckpoint()
    {
        var job = CreateJobAt(CampaignGenerationStage.RenderHero);
        job.BeginStage(Now, TimeSpan.FromMinutes(10));
        job.Fail("render failed", Now.AddSeconds(1));

        job.Retry(Now.AddMinutes(1));

        Assert.Equal(CampaignGenerationStatus.Queued, job.Status);
        Assert.Equal(CampaignGenerationStage.RenderHero, job.CurrentStage);
        Assert.Equal(0, job.StageAttemptCount);
        Assert.Null(job.LastError);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAtUtc);
    }

    [Fact]
    public void RunningCancellationIsObservedAtTheNextWorkerBoundary()
    {
        var job = CreateJob();
        job.BeginStage(Now, TimeSpan.FromMinutes(10));

        job.RequestCancellation(Now.AddSeconds(1));

        Assert.True(job.CancellationRequested);
        Assert.Equal(CampaignGenerationStatus.Running, job.Status);
        Assert.Null(job.CompletedAtUtc);

        job.Cancel(Now.AddSeconds(2));
        Assert.Equal(CampaignGenerationStatus.Cancelled, job.Status);
        Assert.Equal(Now.AddSeconds(2), job.CompletedAtUtc);
    }

    [Fact]
    public void CampaignCompletesOnlyFromTheFinalStage()
    {
        var unfinished = CreateJob();
        unfinished.BeginStage(Now, TimeSpan.FromMinutes(10));
        Assert.Throws<InvalidOperationException>(() => unfinished.Complete(Now));

        var finalizing = CreateJobAt(CampaignGenerationStage.FinalizeCampaign);
        finalizing.BeginStage(Now, TimeSpan.FromMinutes(10));
        finalizing.Complete(Now.AddSeconds(1));

        Assert.Equal(CampaignGenerationStatus.Completed, finalizing.Status);
        Assert.Equal(Now.AddSeconds(1), finalizing.CompletedAtUtc);
        Assert.Null(finalizing.NextAttemptAtUtc);
    }

    private static CampaignGenerationJob CreateJob()
    {
        return CampaignGenerationJob.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "owner-id",
            new string('a', 64),
            Now);
    }

    private static CampaignGenerationJob CreateJobAt(CampaignGenerationStage target)
    {
        var job = CreateJob();
        foreach (var stage in Enum.GetValues<CampaignGenerationStage>().Skip(1))
        {
            job.BeginStage(Now, TimeSpan.FromMinutes(10));
            job.CompleteStage(stage, Now);
            if (stage == target)
            {
                return job;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(target));
    }
}
