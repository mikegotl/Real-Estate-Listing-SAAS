using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Notifications;

/// <summary>
/// Adds the notifications for a finished background operation to the processor's unit of work. They are saved by the
/// same SaveChanges that records the operation's final state, so each finish produces its notifications exactly once.
/// </summary>
public static class JobNotifications
{
    public const string MediaTab = "media";
    public const string CampaignTab = "campaign";

    /// <summary>
    /// Notifies the member who started the operation when it is known and still in the organization, otherwise every
    /// member of the organization.
    /// </summary>
    public static async Task AddAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        string? requestedByUserId,
        JobNotificationContent content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var members = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.OrganizationId == organizationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        var recipients = requestedByUserId is not null && members.Contains(requestedByUserId)
            ? [requestedByUserId]
            : members;
        foreach (var userId in recipients)
        {
            dbContext.UserNotifications.Add(UserNotification.Create(
                organizationId,
                userId,
                propertyId,
                content.Kind,
                content.Outcome,
                content.Title,
                content.Message,
                content.Tab,
                now));
        }
    }

    /// <summary>The property's street address, which is how notifications name it.</summary>
    public static async Task<string> GetPropertyLabelAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken) =>
        await dbContext.Properties.AsNoTracking()
            .Where(property => property.Id == propertyId && property.OrganizationId == organizationId)
            .Select(property => property.Address1)
            .SingleOrDefaultAsync(cancellationToken)
        ?? "your property";
}

public sealed record JobNotificationContent(
    UserNotificationKind Kind,
    UserNotificationOutcome Outcome,
    string Title,
    string Message,
    string Tab);

/// <summary>The words each kind of finished operation is reported with.</summary>
public static class JobNotificationText
{
    public static JobNotificationContent VideoEnhanced(string property, string filename) => new(
        UserNotificationKind.VideoEnhancement,
        UserNotificationOutcome.Succeeded,
        "Video ready",
        $"\"{filename}\" for {property} is stabilized and color corrected.",
        JobNotifications.MediaTab);

    public static JobNotificationContent VideoEnhancementFailed(string property, string filename, string? error) => new(
        UserNotificationKind.VideoEnhancement,
        UserNotificationOutcome.Failed,
        "Video processing failed",
        WithReason($"\"{filename}\" for {property} could not be processed.", error),
        JobNotifications.MediaTab);

    public static JobNotificationContent RoomPhotosFound(string property, string filename, int added) => new(
        UserNotificationKind.RoomPhotos,
        UserNotificationOutcome.Succeeded,
        "Room photos ready",
        added switch
        {
            0 => $"No new room photos were found in \"{filename}\" for {property}.",
            1 => $"1 room photo from \"{filename}\" is in Extra photos for {property}.",
            _ => $"{added} room photos from \"{filename}\" are in Extra photos for {property}.",
        },
        JobNotifications.MediaTab);

    public static JobNotificationContent RoomPhotosFailed(string property, string filename, string? error) => new(
        UserNotificationKind.RoomPhotos,
        UserNotificationOutcome.Failed,
        "Finding room photos failed",
        WithReason($"Room photos could not be found in \"{filename}\" for {property}.", error),
        JobNotifications.MediaTab);

    public static JobNotificationContent WalkthroughClipsFinished(string property, int completed, int failed)
    {
        var total = completed + failed;
        if (failed == 0)
        {
            return new(
                UserNotificationKind.WalkthroughClips,
                UserNotificationOutcome.Succeeded,
                "Walk-through clips ready",
                completed == 1
                    ? $"The walk-through clip for {property} is ready."
                    : $"All {completed} walk-through clips for {property} are ready.",
                JobNotifications.MediaTab);
        }

        return new(
            UserNotificationKind.WalkthroughClips,
            completed == 0 ? UserNotificationOutcome.Failed : UserNotificationOutcome.Succeeded,
            completed == 0 ? "Walk-through clips failed" : "Walk-through clips finished",
            completed == 0
                ? $"{Clips(failed)} for {property} could not be made. You can try again on the Media tab."
                : $"{completed} of {total} walk-through clips for {property} are ready; {failed} could not be made.",
            JobNotifications.MediaTab);
    }

    public static JobNotificationContent CampaignCompleted(string property) => new(
        UserNotificationKind.Campaign,
        UserNotificationOutcome.Succeeded,
        "Campaign ready",
        $"The marketing videos for {property} are ready to review and download.",
        JobNotifications.CampaignTab);

    public static JobNotificationContent CampaignFailed(string property, CampaignGenerationStage stage, string? error) => new(
        UserNotificationKind.Campaign,
        UserNotificationOutcome.Failed,
        "Campaign stopped",
        WithReason($"The campaign for {property} stopped while {StageActivity(stage)}. You can resume it on the Campaign tab.", error),
        JobNotifications.CampaignTab);

    private static string Clips(int count) => count == 1 ? "The walk-through clip" : $"All {count} walk-through clips";

    private static string WithReason(string sentence, string? error) =>
        string.IsNullOrWhiteSpace(error) ? sentence : $"{sentence} {error.Trim()}";

    private static string StageActivity(CampaignGenerationStage stage) => stage switch
    {
        CampaignGenerationStage.ValidateProperty => "checking the property",
        CampaignGenerationStage.AnalyzeMedia => "analyzing photos",
        CampaignGenerationStage.GenerateStory => "writing the story",
        CampaignGenerationStage.GenerateMasterVideoPlan => "planning the video",
        CampaignGenerationStage.GenerateNarration => "recording the narration",
        CampaignGenerationStage.GenerateDerivativePlans => "planning the short edits",
        CampaignGenerationStage.GenerateRequiredAiVideo => "generating AI video",
        CampaignGenerationStage.RenderHero or CampaignGenerationStage.RenderFeature
            or CampaignGenerationStage.RenderTeaser or CampaignGenerationStage.RenderLongForm => "rendering videos",
        CampaignGenerationStage.GenerateSocialCopy => "writing social posts",
        _ => "finishing up",
    };
}
