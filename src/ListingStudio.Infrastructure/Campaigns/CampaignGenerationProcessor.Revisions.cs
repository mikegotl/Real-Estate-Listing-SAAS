using System.Data;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>Re-renders one campaign video with the edits a user approved in its chat.</summary>
public sealed partial class CampaignGenerationProcessor
{
    public async Task<CampaignVideoRevisionRunResult?> ProcessNextRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimNextRevisionAsync(cancellationToken);
        if (claimed is null)
        {
            return null;
        }

        var (revisionId, attemptNumber, timeoutSeconds) = claimed.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await RenderRevisionAsync(revisionId, timeout.Token);
            return new CampaignVideoRevisionRunResult(revisionId, true, false, attemptNumber, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogRevisionFailure(exception, revisionId, attemptNumber);
            dbContext.ChangeTracker.Clear();
            var revision = await dbContext.CampaignVideoRevisions.SingleAsync(
                candidate => candidate.Id == revisionId,
                CancellationToken.None);
            var now = timeProvider.GetUtcNow();
            var error = exception is CampaignVideoRevisionException
                ? exception.Message
                : SanitizeError(exception, timeout.IsCancellationRequested);
            var willRetry = exception is not CampaignVideoRevisionException
                && attemptNumber < CampaignVideoRevision.MaximumAttempts
                && IsRetryable(exception, timeout.IsCancellationRequested);
            if (willRetry)
            {
                revision.ScheduleRetry(error, now, TimeSpan.FromSeconds(Math.Pow(2, attemptNumber)));
            }
            else
            {
                revision.Fail(error, now);
                var job = await GetJobAsync(revision.CampaignGenerationJobId, CancellationToken.None);
                var kind = await dbContext.CampaignDeliverables.AsNoTracking()
                    .Where(candidate => candidate.Id == revision.CampaignDeliverableId)
                    .Select(candidate => candidate.Kind)
                    .SingleAsync(CancellationToken.None);
                await NotifyRevisionAsync(
                    job,
                    revision.RequestedByUserId,
                    property => JobNotificationText.CampaignVideoUpdateFailed(property, CampaignVideoChatService.OutputName(kind), error),
                    now,
                    CancellationToken.None);
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
            return new CampaignVideoRevisionRunResult(revisionId, false, willRetry, attemptNumber, error);
        }
    }

    private async Task<(Guid RevisionId, int AttemptNumber, int TimeoutSeconds)?> ClaimNextRevisionAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.CampaignVideoRevisions.FromSqlInterpolated($"""
            SELECT *
            FROM "CampaignVideoRevisions"
            WHERE (
                ("Status" = 'Queued' AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now}))
                OR ("Status" = 'Running' AND "LeaseExpiresAtUtc" <= {now})
            )
            ORDER BY "CreatedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var revision = candidates.SingleOrDefault();
        if (revision is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var durationSeconds = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(candidate => candidate.Id == revision.CampaignDeliverableId)
            .Select(candidate => (int)candidate.RequestedDuration)
            .SingleAsync(cancellationToken);
        // A revision renders one video, so it gets the time that video's render stage would.
        var timeoutSeconds = CalculateStageTimeoutSeconds(
            options.Value.StageTimeoutSeconds,
            CampaignGenerationStage.RenderLongForm,
            durationSeconds);
        revision.Begin(now, TimeSpan.FromSeconds(Math.Max(options.Value.LeaseSeconds, timeoutSeconds + 60)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = (revision.Id, revision.AttemptCount, timeoutSeconds);
        dbContext.ChangeTracker.Clear();
        return result;
    }

    private async Task RenderRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await dbContext.CampaignVideoRevisions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == revisionId, cancellationToken);
        var deliverable = await dbContext.CampaignDeliverables.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == revision.CampaignDeliverableId
                && candidate.OrganizationId == revision.OrganizationId,
                cancellationToken);
        if (deliverable.Version != revision.BaseVersion || deliverable.Status != CampaignDeliverableStatus.Rendered)
        {
            throw new CampaignVideoRevisionException(
                "The video changed after these edits were suggested. Ask again in the chat.");
        }

        var job = await GetJobAsync(deliverable.CampaignGenerationJobId, cancellationToken);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            deliverable.SpecificationJson,
            SerializerOptions)
            ?? throw new InvalidDataException("The stored video plan is invalid.");
        var context = await CampaignVideoEditing.LoadContextAsync(
            dbContext,
            deliverable.OrganizationId,
            deliverable.PropertyId,
            cancellationToken);
        var edits = CampaignVideoEditing.Deserialize(revision.EditsJson).Edits;
        var result = CampaignVideoEditor.Apply(specification, edits, context);
        if (result.Applied.Count == 0)
        {
            // Photos can be deleted between the suggestion and the render.
            throw new CampaignVideoRevisionException(
                (result.Rejected.Count > 0 ? result.Rejected[0].Description : null) ?? "None of the changes could be made to this video.");
        }

        var edited = result.Specification;
        var narrationId = deliverable.VideoNarrationId;
        if (result.NewVoice is { } accent)
        {
            (edited, narrationId) = await RevoiceAsync(job, revision, deliverable, edited, accent, cancellationToken);
        }

        var assetPath = $"organizations/{job.OrganizationId:N}/properties/{job.PropertyId:N}/campaigns/{job.Id:N}/{deliverable.Kind.ToString().ToLowerInvariant()}-{revision.Id:N}.mp4";
        var json = JsonSerializer.Serialize(edited, SerializerOptions);
        await RenderAndStoreAsync(
            job,
            edited,
            narrationId,
            $"{deliverable.Kind}-revision",
            assetPath,
            async fileSize =>
            {
                dbContext.ChangeTracker.Clear();
                var trackedRevision = await dbContext.CampaignVideoRevisions.SingleAsync(
                    candidate => candidate.Id == revisionId,
                    cancellationToken);
                var trackedDeliverable = await dbContext.CampaignDeliverables.SingleAsync(
                    candidate => candidate.Id == deliverable.Id,
                    cancellationToken);
                if (trackedDeliverable.Version != trackedRevision.BaseVersion)
                {
                    throw new CampaignVideoRevisionException(
                        "The video changed while this update was rendering. Ask again in the chat.");
                }

                var highest = await dbContext.CampaignVideoRevisions
                    .Where(candidate => candidate.CampaignDeliverableId == deliverable.Id)
                    .MaxAsync(candidate => candidate.ResultVersion, cancellationToken) ?? 1;
                var now = timeProvider.GetUtcNow();
                trackedRevision.Complete(
                    trackedDeliverable,
                    json,
                    narrationId,
                    assetPath,
                    "video/mp4",
                    fileSize,
                    Math.Max(highest, trackedDeliverable.Version) + 1,
                    now);
                await NotifyRevisionAsync(
                    job,
                    trackedRevision.RequestedByUserId,
                    property => JobNotificationText.CampaignVideoUpdated(property, CampaignVideoChatService.OutputName(deliverable.Kind)),
                    now,
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Records the same voiceover words with another voice and places the captions on the new timing. The words
    /// stay the ones already approved for this video; only who speaks them changes.
    /// </summary>
    private async Task<(VideoProductionSpecification Specification, Guid NarrationId)> RevoiceAsync(
        CampaignGenerationJob job,
        CampaignVideoRevision revision,
        CampaignDeliverable deliverable,
        VideoProductionSpecification specification,
        CampaignVoiceAccent accent,
        CancellationToken cancellationToken)
    {
        var planId = await dbContext.VideoNarrations.AsNoTracking()
            .Where(candidate => candidate.Id == deliverable.VideoNarrationId
                && candidate.OrganizationId == deliverable.OrganizationId)
            .Select(candidate => candidate.VideoProductionPlanId)
            .SingleAsync(cancellationToken);
        var preferences = NarrationPreferences(job);
        VideoNarrationResult narration;
        try
        {
            narration = await narrationService.GenerateAsync(
                revision.RequestedByUserId,
                planId,
                new NarrationGenerationOptions(
                    specification.Audio.NarrationSegments.Select(segment => new VoiceNarrationSegment(
                        segment.Id,
                        segment.Text,
                        segment.StartMs,
                        segment.DurationMs)).ToArray(),
                    preferences.SpokenLanguageCode,
                    $"Use a natural {Display(accent)} accent with pronunciation and delivery appropriate to {Display(job.NarrationRegion)}. Do not imitate a person or caricature a regional identity.",
                    accent.ToString()),
                cancellationToken)
                ?? throw new InvalidOperationException("The new voiceover could not be recorded.");
        }
        catch (InvalidDataException exception) when (NarrationRunsPastVideo(exception))
        {
            throw new CampaignVideoRevisionException(
                "That voice speaks too slowly to fit this video. Try another voice.");
        }

        var style = CampaignVideoEditor.CaptionStyleOf(specification);
        if (style is not null)
        {
            specification = CampaignCreativeDirector.ReplaceCaptions(
                specification,
                style.Value,
                CaptionText(job, deliverable.Kind),
                narration.Timing);
        }

        return (specification, narration.Id);
    }

    /// <summary>The caption wording for each voiceover line, which may be in another language than the voice.</summary>
    private static Dictionary<string, string> CaptionText(CampaignGenerationJob job, CampaignOutputKind kind)
    {
        var copies = kind is CampaignOutputKind.TikTok or CampaignOutputKind.InstagramReel or CampaignOutputKind.YouTubeShort
            ? new[] { job.ShortFormLocalizedCopyJson, job.LocalizedCopyJson }
            : kind is CampaignOutputKind.FullNarratedTour or CampaignOutputKind.LongForm
                ? new[] { job.LongFormLocalizedCopyJson }
                : new[] { job.LocalizedCopyJson };
        var captions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var json in copies.Where(json => !string.IsNullOrWhiteSpace(json)))
        {
            var copy = JsonSerializer.Deserialize<CampaignLocalizedCopy>(json!, AudioSerializerOptions);
            foreach (var segment in copy?.CaptionSegments ?? [])
            {
                captions.TryAdd(segment.Id, segment.Text);
            }
        }

        return captions;
    }

    private async Task NotifyRevisionAsync(
        CampaignGenerationJob job,
        string userId,
        Func<string, JobNotificationContent> content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var property = await JobNotifications.GetPropertyLabelAsync(
            dbContext, job.OrganizationId, job.PropertyId, cancellationToken);
        await JobNotifications.AddAsync(
            dbContext,
            job.OrganizationId,
            job.PropertyId,
            userId,
            content(property),
            now,
            cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign video revision {RevisionId} failed on attempt {AttemptNumber}")]
    private partial void LogRevisionFailure(Exception exception, Guid revisionId, int attemptNumber);
}

/// <summary>A revision that cannot succeed by retrying; its message is shown to the user as is.</summary>
public sealed class CampaignVideoRevisionException(string message) : Exception(message);
