using System.Data;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Notifications;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>
/// Advances one drone intro per call: submits a pending intro to the provider, or checks on one the provider is still
/// generating, then plays it backwards with the AI label burned in and stores it for the agent to review.
/// </summary>
public sealed partial class AerialIntroProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IWalkthroughVideoProvider provider,
    IAerialIntroVideoEditor editor,
    IAiUsagePropertyContext aiUsagePropertyContext,
    TimeProvider timeProvider,
    ILogger<AerialIntroProcessor> logger) : IAerialIntroProcessor
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SubmissionLease = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaximumGenerationTime = TimeSpan.FromMinutes(30);

    public async Task<AerialIntroRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!provider.IsEnabled)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(SubmissionLease);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.AerialIntroClips.FromSqlInterpolated($"""
            SELECT *
            FROM "AerialIntroClips"
            WHERE ("Status" = 'Pending' AND ("NextCheckAtUtc" IS NULL OR "NextCheckAtUtc" <= {now}))
               OR ("Status" = 'Failed'
                   AND "AttemptCount" < {AerialIntroClip.MaximumAttempts}
                   AND "NextCheckAtUtc" <= {now})
               OR ("Status" = 'Generating' AND "NextCheckAtUtc" <= {now})
               OR ("Status" = 'Submitting' AND "LastAttemptedAtUtc" <= {staleBefore})
            ORDER BY "NextCheckAtUtc" NULLS FIRST, "CreatedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var clip = candidates.SingleOrDefault();
        if (clip is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (clip.Status == AerialIntroClipStatus.Generating)
        {
            // Hold the claim for the length of one check so a second worker does not poll the same request.
            clip.ScheduleCheck(now.Add(SubmissionLease));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await CheckAsync(clip, cancellationToken);
        }

        if (clip.Status == AerialIntroClipStatus.Submitting && clip.AttemptCount >= AerialIntroClip.MaximumAttempts)
        {
            clip.Fail("The drone intro submission did not finish.", null);
            await NotifyAsync(clip, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(clip);
        }

        clip.BeginSubmission(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await SubmitAsync(clip, cancellationToken);
    }

    private async Task<AerialIntroRunResult> SubmitAsync(AerialIntroClip claimed, CancellationToken cancellationToken)
    {
        var (clipId, organizationId, attempt) = (claimed.Id, claimed.OrganizationId, claimed.AttemptCount);
        dbContext.ChangeTracker.Clear();
        try
        {
            var mimeType = await dbContext.PropertyMedia.AsNoTracking()
                .Where(media => media.Id == claimed.PropertyMediaId
                    && media.OrganizationId == organizationId
                    && media.PropertyId == claimed.PropertyId)
                .Select(media => media.BlobPath == claimed.SourceBlobPath
                    ? media.MimeType
                    : media.OriginalBlobPath == claimed.SourceBlobPath ? media.OriginalMimeType : null)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new FileNotFoundException("The Exterior front photo for this drone intro changed or no longer exists.");
            // The intro is made from the photo file the agent saw when asking, even if a fix has replaced it since.
            await using var photo = await propertyMediaStorage.OpenReadAsync(claimed.SourceBlobPath, cancellationToken)
                ?? throw new FileNotFoundException("The Exterior front photo could not be opened.");
            using var attribution = aiUsagePropertyContext.BeginProperty(claimed.PropertyId);
            var requestId = await provider.SubmitAerialIntroAsync(
                new AerialIntroVideoSubmission(
                    new WalkthroughVideoFrame(photo, NormalizeImageType(mimeType)),
                    claimed.HeightFeet,
                    $"{clipId:N}-{attempt}"),
                cancellationToken);
            var current = await LoadClaimAsync(clipId, organizationId, attempt, cancellationToken);
            var now = timeProvider.GetUtcNow();
            current.MarkSubmitted(requestId, now, now.Add(CheckInterval));
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result(current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSubmissionFailed(exception, clipId, attempt);
            return await FailAsync(clipId, organizationId, attempt, Sanitize(exception), cancellationToken);
        }
    }

    private async Task<AerialIntroRunResult> CheckAsync(AerialIntroClip claimed, CancellationToken cancellationToken)
    {
        var (clipId, organizationId, attempt) = (claimed.Id, claimed.OrganizationId, claimed.AttemptCount);
        var requestId = claimed.ProviderRequestId
            ?? throw new InvalidOperationException("A generating drone intro has no provider request.");
        var timedOut = timeProvider.GetUtcNow() - claimed.SubmittedAtUtc > MaximumGenerationTime;
        dbContext.ChangeTracker.Clear();
        WalkthroughVideoPollResult poll;
        try
        {
            poll = await provider.PollAsync(requestId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCheckFailed(exception, clipId, attempt);
            return timedOut
                ? await FailAsync(clipId, organizationId, attempt, Sanitize(exception), cancellationToken)
                : await ScheduleCheckAsync(clipId, organizationId, attempt, cancellationToken);
        }

        await using var content = poll.Content;
        switch (poll.State)
        {
            case WalkthroughVideoState.InProgress when timedOut:
                return await FailAsync(
                    clipId, organizationId, attempt, "The drone intro took too long to generate.", cancellationToken);
            case WalkthroughVideoState.InProgress:
                return await ScheduleCheckAsync(clipId, organizationId, attempt, cancellationToken);
            case WalkthroughVideoState.Failed:
                return await FailAsync(
                    clipId,
                    organizationId,
                    attempt,
                    poll.FailureReason ?? "The drone intro could not be generated.",
                    cancellationToken);
        }

        if (content is null)
        {
            return await FailAsync(clipId, organizationId, attempt, "The provider returned no clip.", cancellationToken);
        }

        var assetPath = $"organizations/{organizationId:N}/properties/{claimed.PropertyId:N}/aerial-intros/{clipId:N}-{attempt}.mp4";
        var stored = false;
        try
        {
            var edited = await editor.PrepareAsync(content, cancellationToken);
            await using (edited.Content)
            {
                await campaignAssetStorage.StoreAsync(assetPath, edited.Content, "video/mp4", cancellationToken);
            }

            stored = true;
            return await FinishAsync(
                clipId,
                organizationId,
                attempt,
                current => current.Complete(
                    poll.Provider,
                    poll.Model,
                    assetPath,
                    "video/mp4",
                    edited.FileSize,
                    edited.DurationMs,
                    timeProvider.GetUtcNow()),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            if (stored)
            {
                await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
            }

            LogCheckFailed(exception, clipId, attempt);
            dbContext.ChangeTracker.Clear();
            return await FailAsync(clipId, organizationId, attempt, Sanitize(exception), cancellationToken);
        }
    }

    private async Task<AerialIntroRunResult> ScheduleCheckAsync(
        Guid clipId,
        Guid organizationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var current = await LoadClaimAsync(clipId, organizationId, attempt, cancellationToken);
        current.ScheduleCheck(timeProvider.GetUtcNow().Add(CheckInterval));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(current);
    }

    private async Task<AerialIntroRunResult> FailAsync(
        Guid clipId,
        Guid organizationId,
        int attempt,
        string error,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        return await FinishAsync(
            clipId,
            organizationId,
            attempt,
            current => current.Fail(error, timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, attempt - 1))),
            cancellationToken);
    }

    /// <summary>Records the intro's result and tells the agent when it is ready to review or failed for good.</summary>
    private async Task<AerialIntroRunResult> FinishAsync(
        Guid clipId,
        Guid organizationId,
        int attempt,
        Action<AerialIntroClip> finish,
        CancellationToken cancellationToken)
    {
        var current = await LoadClaimAsync(clipId, organizationId, attempt, cancellationToken);
        finish(current);
        await NotifyAsync(current, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(current);
    }

    private async Task NotifyAsync(AerialIntroClip clip, CancellationToken cancellationToken)
    {
        if (clip.Status is not (AerialIntroClipStatus.Completed or AerialIntroClipStatus.Failed) || clip.CanRetry)
        {
            return;
        }

        var property = await JobNotifications.GetPropertyLabelAsync(
            dbContext, clip.OrganizationId, clip.PropertyId, cancellationToken);
        await JobNotifications.AddAsync(
            dbContext,
            clip.OrganizationId,
            clip.PropertyId,
            clip.RequestedByUserId,
            clip.Status == AerialIntroClipStatus.Completed
                ? JobNotificationText.AerialIntroReady(property)
                : JobNotificationText.AerialIntroFailed(property, clip.LastError),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    // A replaced or removed intro is gone, so its claim ends; the agent's newer intro is claimed on its own.
    private async Task<AerialIntroClip> LoadClaimAsync(
        Guid clipId,
        Guid organizationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var clip = await dbContext.AerialIntroClips.SingleOrDefaultAsync(
            candidate => candidate.Id == clipId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (clip is null
            || clip.AttemptCount != attempt
            || clip.Status is not (AerialIntroClipStatus.Submitting or AerialIntroClipStatus.Generating))
        {
            throw new InvalidOperationException("The drone intro claim is no longer current.");
        }

        return clip;
    }

    private static AerialIntroRunResult Result(AerialIntroClip clip) =>
        new(clip.Id, clip.Status, clip.AttemptCount, clip.LastError);

    private static string NormalizeImageType(string mimeType) =>
        string.Equals(mimeType, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mimeType.ToLowerInvariant();

    private static string Sanitize(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        TimeoutException => "The video provider did not respond in time.",
        HttpRequestException => "The video provider rejected or could not complete the request.",
        InvalidDataException => "The video provider returned an invalid clip.",
        _ => "The drone intro could not be generated.",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drone intro {ClipId} submission attempt {AttemptNumber} failed")]
    private partial void LogSubmissionFailed(Exception exception, Guid clipId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drone intro {ClipId} check on attempt {AttemptNumber} failed")]
    private partial void LogCheckFailed(Exception exception, Guid clipId, int attemptNumber);
}
