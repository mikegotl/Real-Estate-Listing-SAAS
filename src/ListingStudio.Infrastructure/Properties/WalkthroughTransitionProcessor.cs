using System.Data;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>
/// Advances one walk-through clip per call: submits a pending pair of shots to the provider, or checks on a clip
/// the provider is still generating and stores it once it is ready.
/// </summary>
public sealed partial class WalkthroughTransitionProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IWalkthroughVideoProvider provider,
    TimeProvider timeProvider,
    ILogger<WalkthroughTransitionProcessor> logger) : IWalkthroughTransitionProcessor
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SubmissionLease = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaximumGenerationTime = TimeSpan.FromMinutes(30);

    public async Task<WalkthroughTransitionRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
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
        var candidates = await dbContext.WalkthroughTransitions.FromSqlInterpolated($"""
            SELECT *
            FROM "WalkthroughTransitions"
            WHERE "Status" = 'Pending'
               OR ("Status" = 'Failed'
                   AND "AttemptCount" < {WalkthroughTransition.MaximumAttempts}
                   AND "NextCheckAtUtc" <= {now})
               OR ("Status" = 'Generating' AND "NextCheckAtUtc" <= {now})
               OR ("Status" = 'Submitting' AND "LastAttemptedAtUtc" <= {staleBefore})
            ORDER BY "NextCheckAtUtc" NULLS FIRST, "CreatedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var transition = candidates.SingleOrDefault();
        if (transition is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (transition.Status == WalkthroughTransitionStatus.Generating)
        {
            // Hold the claim for the length of one check so a second worker does not poll the same request.
            transition.ScheduleCheck(now.Add(SubmissionLease));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await CheckAsync(transition, cancellationToken);
        }

        if (transition.Status == WalkthroughTransitionStatus.Submitting
            && transition.AttemptCount >= WalkthroughTransition.MaximumAttempts)
        {
            transition.Fail("The walk-through clip submission did not finish.", null);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(transition);
        }

        transition.BeginSubmission(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await SubmitAsync(transition, cancellationToken);
    }

    private async Task<WalkthroughTransitionRunResult> SubmitAsync(
        WalkthroughTransition claimed,
        CancellationToken cancellationToken)
    {
        var (transitionId, organizationId, attempt) = (claimed.Id, claimed.OrganizationId, claimed.AttemptCount);
        dbContext.ChangeTracker.Clear();
        try
        {
            var shots = await dbContext.PropertyMedia.AsNoTracking()
                .Where(media => media.OrganizationId == organizationId
                    && media.PropertyId == claimed.PropertyId
                    && (media.Id == claimed.FromPropertyMediaId || media.Id == claimed.ToPropertyMediaId))
                .ToDictionaryAsync(media => media.Id, cancellationToken);
            if (!shots.TryGetValue(claimed.FromPropertyMediaId, out var from)
                || !shots.TryGetValue(claimed.ToPropertyMediaId, out var to))
            {
                throw new FileNotFoundException("A storyboard shot for this walk-through no longer exists.");
            }

            await using var start = await propertyMediaStorage.OpenReadAsync(from.BlobPath, cancellationToken)
                ?? throw new FileNotFoundException("The first shot's photo could not be opened.");
            await using var end = await propertyMediaStorage.OpenReadAsync(to.BlobPath, cancellationToken)
                ?? throw new FileNotFoundException("The next shot's photo could not be opened.");
            var requestId = await provider.SubmitAsync(
                new WalkthroughVideoSubmission(
                    new WalkthroughVideoFrame(start, NormalizeImageType(from.MimeType)),
                    new WalkthroughVideoFrame(end, NormalizeImageType(to.MimeType)),
                    $"{transitionId:N}-{attempt}",
                    WalkthroughClipDirection.FromVersion(claimed.GenerationVersion)),
                cancellationToken);
            var current = await LoadClaimAsync(transitionId, organizationId, attempt, cancellationToken);
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
            LogSubmissionFailed(exception, transitionId, attempt);
            return await FailAsync(transitionId, organizationId, attempt, Sanitize(exception), cancellationToken);
        }
    }

    private async Task<WalkthroughTransitionRunResult> CheckAsync(
        WalkthroughTransition claimed,
        CancellationToken cancellationToken)
    {
        var (transitionId, organizationId, attempt) = (claimed.Id, claimed.OrganizationId, claimed.AttemptCount);
        var requestId = claimed.ProviderRequestId
            ?? throw new InvalidOperationException("A generating walk-through transition has no provider request.");
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
            LogCheckFailed(exception, transitionId, attempt);
            if (timedOut)
            {
                return await FailAsync(transitionId, organizationId, attempt, Sanitize(exception), cancellationToken);
            }

            return await ScheduleCheckAsync(transitionId, organizationId, attempt, cancellationToken);
        }

        await using var content = poll.Content;
        switch (poll.State)
        {
            case WalkthroughVideoState.InProgress when timedOut:
                return await FailAsync(
                    transitionId,
                    organizationId,
                    attempt,
                    "The walk-through clip took too long to generate.",
                    cancellationToken);
            case WalkthroughVideoState.InProgress:
                return await ScheduleCheckAsync(transitionId, organizationId, attempt, cancellationToken);
            case WalkthroughVideoState.Failed:
                return await FailAsync(
                    transitionId,
                    organizationId,
                    attempt,
                    poll.FailureReason ?? "The walk-through clip could not be generated.",
                    cancellationToken);
        }

        if (content is null)
        {
            return await FailAsync(
                transitionId, organizationId, attempt, "The provider returned no clip.", cancellationToken);
        }

        var assetPath = $"organizations/{organizationId:N}/properties/{claimed.PropertyId:N}/walkthrough-transitions/{transitionId:N}-{attempt}.mp4";
        var stored = false;
        try
        {
            var fileSize = content.CanSeek ? content.Length : 0;
            await campaignAssetStorage.StoreAsync(assetPath, content, "video/mp4", cancellationToken);
            stored = true;
            var current = await LoadClaimAsync(transitionId, organizationId, attempt, cancellationToken);
            current.Complete(
                poll.Provider,
                poll.Model,
                assetPath,
                "video/mp4",
                fileSize,
                provider.ClipDurationMs,
                timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result(current);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            if (stored)
            {
                await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
            }

            LogCheckFailed(exception, transitionId, attempt);
            dbContext.ChangeTracker.Clear();
            return await FailAsync(transitionId, organizationId, attempt, Sanitize(exception), cancellationToken);
        }
    }

    private async Task<WalkthroughTransitionRunResult> ScheduleCheckAsync(
        Guid transitionId,
        Guid organizationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var current = await LoadClaimAsync(transitionId, organizationId, attempt, cancellationToken);
        current.ScheduleCheck(timeProvider.GetUtcNow().Add(CheckInterval));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(current);
    }

    private async Task<WalkthroughTransitionRunResult> FailAsync(
        Guid transitionId,
        Guid organizationId,
        int attempt,
        string error,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var current = await LoadClaimAsync(transitionId, organizationId, attempt, cancellationToken);
        current.Fail(error, timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, attempt - 1)));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(current);
    }

    private async Task<WalkthroughTransition> LoadClaimAsync(
        Guid transitionId,
        Guid organizationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var transition = await dbContext.WalkthroughTransitions.SingleOrDefaultAsync(
            candidate => candidate.Id == transitionId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (transition is null
            || transition.AttemptCount != attempt
            || transition.Status is not (WalkthroughTransitionStatus.Submitting or WalkthroughTransitionStatus.Generating))
        {
            throw new InvalidOperationException("The walk-through transition claim is no longer current.");
        }

        return transition;
    }

    private static WalkthroughTransitionRunResult Result(WalkthroughTransition transition) =>
        new(transition.Id, transition.Status, transition.AttemptCount, transition.LastError);

    private static string NormalizeImageType(string mimeType) =>
        string.Equals(mimeType, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mimeType.ToLowerInvariant();

    private static string Sanitize(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        TimeoutException => "The walk-through provider did not respond in time.",
        HttpRequestException => "The walk-through provider rejected or could not complete the request.",
        InvalidDataException => "The walk-through provider returned an invalid response.",
        _ => "The walk-through clip could not be generated.",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through transition {TransitionId} submission attempt {AttemptNumber} failed")]
    private partial void LogSubmissionFailed(Exception exception, Guid transitionId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through transition {TransitionId} check on attempt {AttemptNumber} failed")]
    private partial void LogCheckFailed(Exception exception, Guid transitionId, int attemptNumber);
}
