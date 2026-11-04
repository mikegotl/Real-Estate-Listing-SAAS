using ListingStudio.Application.Videos;
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
        foreach (var variant in open)
        {
            try
            {
                if (variant.KlingProviderRequestId is null)
                {
                    await SubmitLibraryKlingVideoAsync(job, library, variant, cancellationToken);
                    continue;
                }

                await PollLibraryKlingVideoAsync(job, library, variant, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && (exception is KlingVideoFailedException || IsVariantFailure(exception, job)))
            {
                await ResetVariantKlingAsync(job, variant.Id, cancellationToken);
                await FailVariantAsync(job, variant.Id, exception, cancellationToken);
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
                variant.DurationSeconds);
            var prompt = walkthroughVideoProvider.BuildNativePresenterPrompt(submission);
            var requestId = await walkthroughVideoProvider.SubmitNativePresenterAsync(submission, cancellationToken);
            dbContext.ChangeTracker.Clear();
            var tracked = await dbContext.CampaignVideoVariants.SingleAsync(
                candidate => candidate.Id == variant.Id && candidate.OrganizationId == job.OrganizationId,
                cancellationToken);
            tracked.MarkKlingSubmitted(
                requestId,
                $"{walkthroughVideoProvider.NativePresenterGenerationVersion}/{variant.DurationSeconds}s",
                prompt,
                timeProvider.GetUtcNow());
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
            throw new KlingVideoFailedException(result.FailureReason ?? "Higgsfield could not generate the Kling 4 video.");
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
        Level = LogLevel.Information,
        Message = "Campaign {JobId} library {LibraryId} submitted Kling 4 native presenter for video {VariantId} ({Format}, {Seconds} s)")]
    private partial void LogLibraryKlingSubmitted(
        Guid jobId,
        Guid libraryId,
        Guid variantId,
        VerticalCreativeFormat format,
        int seconds);
}
