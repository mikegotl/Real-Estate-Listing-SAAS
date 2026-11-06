using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Providers;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Kling 4 native presenter for the vertical video library: every library video of 30 seconds or less gets its own
/// Kling call with its own length, opening shot and script. Longer videos keep the established edit. A video whose
/// Kling request fails is marked as failed on its own, so the other videos still finish; the campaign fails only when
/// none of its Kling videos could be made, because it never quietly swaps the pilot for the control edit.
/// </summary>
public sealed partial class CampaignGenerationProcessor
{
    /// <summary>Whether a library video is made with its own Kling 4 native-presenter call.</summary>
    public static bool UsesOwnKlingVideo(CampaignVideoVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return variant.IsEligible
            && variant.DurationSeconds is >= NativePresenterVideoPolicy.MinimumDurationSeconds
                and <= NativePresenterVideoPolicy.MaximumDurationSeconds;
    }

    /// <summary>
    /// Pilot: the native-presenter switch is on for this workspace through the pilot list, not for every workspace.
    /// </summary>
    private async Task<bool> IsKlingNativePresenterPilotAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var state = await featureStateService.GetAsync(FeatureFlagKeys.Kling4NativePresenterV1, cancellationToken);
        return KlingNativePresenterRelease.IsPilot(state, organizationId);
    }

    /// <summary>Where a library video's finished Kling 4 video is kept.</summary>
    public static string VariantKlingAssetPath(
        Guid organizationId,
        Guid propertyId,
        Guid jobId,
        Guid libraryId,
        Guid variantId) =>
        $"organizations/{organizationId:N}/properties/{propertyId:N}/campaigns/{jobId:N}/library/{libraryId:N}"
        + $"/variants/{variantId:N}/kling4-native-presenter.mp4";

    /// <summary>
    /// Submits and polls one Kling call per library video. Returns true once every Kling video finished or failed.
    /// </summary>
    private async Task<bool> GenerateLibraryKlingVideosAsync(
        CampaignGenerationJob job,
        CampaignVideoLibrary library,
        CancellationToken cancellationToken)
    {
        var videos = library.Variants
            .Where(UsesOwnKlingVideo)
            .OrderBy(variant => variant.Sequence)
            .ToArray();
        if (videos.Length == 0)
        {
            throw new InvalidOperationException(
                "The Kling 4 native presenter needs a vertical video of 30 seconds or less. No fallback video was rendered.");
        }

        var open = videos
            .Where(variant => variant.Status is CampaignVideoVariantStatus.Planned or CampaignVideoVariantStatus.Generating
                && variant.KlingAssetPath is null)
            .ToArray();
        // One Kling request at a time: Higgsfield refuses a new generation while the account's previous one is still
        // running, so the next video is submitted only after the one before it has finished.
        var inFlight = open.Any(variant => variant.KlingProviderRequestId is not null);
        foreach (var variant in open)
        {
            try
            {
                if (variant.KlingProviderRequestId is null)
                {
                    if (inFlight)
                    {
                        continue;
                    }

                    await SubmitLibraryKlingVideoAsync(job, library, variant, cancellationToken);
                    inFlight = true;
                    continue;
                }

                await PollLibraryKlingVideoAsync(job, library, variant, cancellationToken);
            }
            catch (HttpRequestException exception) when (IsProviderBusy(exception)
                && timeProvider.GetUtcNow() - library.CreatedAtUtc < MaximumKlingQueueTime)
            {
                // Busy or rate-limited: wait and submit again at the next poll instead of spending the stage's retries.
                LogLibraryKlingBusy(job.Id, variant.Id, (int?)exception.StatusCode);
                inFlight = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && (exception is KlingVideoFailedException || IsVariantFailure(exception, job)))
            {
                await ResetVariantKlingAsync(job, variant.Id, cancellationToken);
                await FailVariantAsync(job, variant.Id, KlingFailure(exception), cancellationToken);
            }
        }

        dbContext.ChangeTracker.Clear();
        var states = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(candidate => candidate.CampaignVideoLibraryId == library.Id
                && candidate.OrganizationId == job.OrganizationId)
            .ToArrayAsync(cancellationToken);
        var kling = states.Where(UsesOwnKlingVideo).ToArray();
        if (kling.Any(variant => variant.Status is CampaignVideoVariantStatus.Planned or CampaignVideoVariantStatus.Generating
            && variant.KlingAssetPath is null))
        {
            return false;
        }

        if (!kling.Any(variant => variant.KlingAssetPath is not null))
        {
            var reason = kling.Select(variant => variant.Error).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
            throw new InvalidOperationException(
                $"None of the Kling 4 videos could be made{(reason is null ? "." : $": {reason}")} No fallback video was rendered.");
        }

        return true;
    }

    private async Task SubmitLibraryKlingVideoAsync(
        CampaignGenerationJob job,
        CampaignVideoLibrary library,
        CampaignVideoVariant variant,
        CancellationToken cancellationToken)
    {
        if (!job.AgentAvatarConsentConfirmed || job.AgentPortraitId is not { } portraitId)
        {
            throw new InvalidOperationException("The Kling 4 native presenter requires an approved profile headshot and consent.");
        }

        var approved = job.PresenterScriptFor(variant.Format);
        var plan = await NativePresenterRequestPlanner.BuildAsync(
            dbContext,
            new NativePresenterPlanInput(
                job.OrganizationId,
                job.PropertyId,
                job.VerticalGoal,
                job.MarketingTemplate,
                job.OpenHouseText,
                job.Soundtrack,
                approved,
                variant.Format,
                variant.DurationSeconds,
                variant.PrimaryPropertyMediaId),
            cancellationToken);
        if (plan.Frames.Count < NativePresenterVideoPolicy.MinimumTourFrames)
        {
            throw new KlingVideoFailedException("The Kling 4 native presenter needs at least one approved Tour Map photo.");
        }

        try
        {
            plan.Script.Validate(variant.DurationSeconds);
        }
        catch (ArgumentException exception)
        {
            throw new KlingVideoFailedException(exception.Message, exception);
        }

        var portrait = await dbContext.AgentPortraits.AsNoTracking()
            .Where(candidate => candidate.Id == portraitId
                && candidate.OrganizationId == job.OrganizationId
                && candidate.UserId == job.RequestedByUserId)
            .Select(candidate => new { candidate.BlobPath, candidate.MimeType })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The campaign's approved agent photo is unavailable.");
        var streams = new List<Stream>(plan.Frames.Count + 1);
        try
        {
            var agentPortrait = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
                ?? throw new FileNotFoundException("The campaign's approved agent photo could not be opened.");
            streams.Add(agentPortrait);
            var frames = new List<NativePresenterTourFrame>(plan.Frames.Count);
            foreach (var frame in plan.Frames)
            {
                var stream = await propertyMediaStorage.OpenReadAsync(frame.BlobPath, cancellationToken)
                    ?? throw new FileNotFoundException("A Tour Map photo could not be opened.");
                streams.Add(stream);
                frames.Add(new NativePresenterTourFrame(
                    new WalkthroughVideoFrame(stream, frame.MimeType),
                    frame.StopName,
                    frame.ApprovedHighlight,
                    frame.DirectionToNext));
            }

            var submission = new NativePresenterVideoSubmission(
                new WalkthroughVideoFrame(agentPortrait, portrait.MimeType),
                frames,
                plan.Script.Opening,
                plan.Script.Closing,
                plan.CampaignGoal,
                plan.MusicDirection,
                $"campaign-native-presenter-{job.Id:N}-{variant.Id:N}",
                plan.Script.Tour,
                variant.DurationSeconds,
                NativePresenterVideoPolicy.ResolutionFor(
                    await IsKlingNativePresenterPilotAsync(job.OrganizationId, cancellationToken)));
            var prompt = walkthroughVideoProvider.BuildNativePresenterPrompt(submission);
            var submitted = await walkthroughVideoProvider.SubmitNativePresenterAsync(submission, cancellationToken);
            dbContext.ChangeTracker.Clear();
            var tracked = await dbContext.CampaignVideoVariants.SingleAsync(
                candidate => candidate.Id == variant.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
            tracked.MarkKlingSubmitted(
                submitted.RequestId,
                $"{walkthroughVideoProvider.NativePresenterGenerationVersion}/{variant.DurationSeconds}s",
                prompt,
                timeProvider.GetUtcNow(),
                submitted.Request.BodyJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            LogLibraryKlingSubmitted(job.Id, library.Id, variant.Id, variant.Format, variant.DurationSeconds);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private async Task PollLibraryKlingVideoAsync(
        CampaignGenerationJob job,
        CampaignVideoLibrary library,
        CampaignVideoVariant variant,
        CancellationToken cancellationToken)
    {
        if (variant.KlingSubmittedAtUtc is null
            || timeProvider.GetUtcNow() - variant.KlingSubmittedAtUtc > TimeSpan.FromMinutes(30))
        {
            throw new KlingVideoFailedException("The Kling 4 video exceeded its processing time limit.");
        }

        var result = await walkthroughVideoProvider.PollAsync(variant.KlingProviderRequestId!, cancellationToken);
        if (result.State == WalkthroughVideoState.InProgress)
        {
            return;
        }

        if (result.State == WalkthroughVideoState.Failed || result.Content is null)
        {
            var reason = result.FailureReason ?? "Higgsfield could not generate the Kling 4 video.";
            dbContext.ChangeTracker.Clear();
            RecordProviderResult(
                job,
                variant.Id,
                $"make the {VerticalFormatNames.Name(variant.Format)} Kling 4 video",
                reason,
                variant.KlingProviderRequestId,
                variant.KlingSubmittedRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new KlingVideoFailedException(reason);
        }

        await using var content = result.Content;
        var assetPath = VariantKlingAssetPath(job.OrganizationId, job.PropertyId, job.Id, library.Id, variant.Id);
        long fileSize;
        if (content.CanSeek)
        {
            fileSize = content.Length;
            await campaignAssetStorage.StoreAsync(assetPath, content, "video/mp4", cancellationToken);
        }
        else
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            fileSize = buffer.Length;
            await campaignAssetStorage.StoreAsync(assetPath, buffer, "video/mp4", cancellationToken);
        }

        try
        {
            dbContext.ChangeTracker.Clear();
            var tracked = await dbContext.CampaignVideoVariants.SingleAsync(
                candidate => candidate.Id == variant.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
            tracked.MarkKlingCompleted(assetPath, fileSize, variant.DurationSeconds * 1_000, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
        catch
        {
            await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
            throw;
        }
    }

    private async Task ResetVariantKlingAsync(CampaignGenerationJob job, Guid variantId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var tracked = await dbContext.CampaignVideoVariants.SingleAsync(
            candidate => candidate.Id == variantId && candidate.OrganizationId == job.OrganizationId,
            cancellationToken);
        tracked.ResetKling(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    /// <summary>How long a library's Kling videos may wait for Higgsfield to accept them before they are failed.</summary>
    private static readonly TimeSpan MaximumKlingQueueTime = TimeSpan.FromHours(3);

    private static bool IsProviderBusy(HttpRequestException exception) => exception.StatusCode is
        System.Net.HttpStatusCode.TooManyRequests
        or System.Net.HttpStatusCode.Conflict
        or System.Net.HttpStatusCode.ServiceUnavailable;

    /// <summary>Keeps the provider's HTTP status in the video's error, so a refused request says why.</summary>
    private static Exception KlingFailure(Exception exception) => exception switch
    {
        ProviderRequestException provider => new KlingVideoFailedException(provider.Describe(500), exception),
        HttpRequestException { StatusCode: { } status } => new KlingVideoFailedException(
            $"Higgsfield refused the Kling 4 request (HTTP {(int)status}).",
            exception),
        _ => exception,
    };

    /// <summary>A Kling request that cannot succeed by trying the same request again.</summary>
    private sealed class KlingVideoFailedException : InvalidOperationException
    {
        public KlingVideoFailedException(string message)
            : base(message)
        {
        }

        public KlingVideoFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} Kling 4 video {VariantId} was refused as busy (HTTP {StatusCode}); submitting again later")]
    private partial void LogLibraryKlingBusy(Guid jobId, Guid variantId, int? statusCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Campaign {JobId} Kling 4 video {VariantId}: the campaign voice is longer than the pilot video, so only Kling's own sound is used")]
    private partial void LogKlingVariantWithoutCampaignVoice(Guid jobId, Guid variantId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Campaign {JobId} library {LibraryId} submitted Kling 4 native presenter for video {VariantId} ({Format}, {Seconds} s)")]
    private partial void LogLibraryKlingSubmitted(
        Guid jobId,
        Guid libraryId,
        Guid variantId,
        VerticalCreativeFormat format,
        int seconds);
}
