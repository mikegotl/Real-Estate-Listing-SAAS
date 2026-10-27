using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>
/// A voiceover script the agent supplied, which the video speaks word for word in place of the AI script: the
/// room-by-room narration when the agent chose it, otherwise an uploaded script accepted for marketing.
/// </summary>
public sealed record UserNarration(
    Guid SourceId,
    DateTimeOffset AcceptedAtUtc,
    bool IsRoomNarration,
    IReadOnlyList<AcceptedNarrationScriptSegment> Segments)
{
    public string Text => RoomNarrationScript.FullText(Segments);

    public bool RequiresLongForm => NarrationScriptPolicy.RequiresLongForm(Text);

    public string SourceReference(AcceptedNarrationScriptSegment segment) => IsRoomNarration
        ? $"WalkthroughStopNarration:{segment.WalkthroughStop}"
        : SourceId.ToString("D");

    public AcceptedNarrationScriptInput ToDirectionInput() => new(SourceId, AcceptedAtUtc, Segments);
}

public static class UserNarrationSource
{
    public static async Task<UserNarration?> LoadAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var choice = await dbContext.Properties
            .AsNoTracking()
            .Where(property => property.OrganizationId == organizationId && property.Id == propertyId)
            .Select(property => new { property.UseRoomNarration, property.NarrationSourceChosenAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (choice?.UseRoomNarration == true)
        {
            var room = await LoadRoomNarrationAsync(dbContext, organizationId, propertyId, cancellationToken);
            if (room.Segments.Count > 0)
            {
                var acceptedAt = room.LatestEditUtc is { } edited && edited > choice.NarrationSourceChosenAtUtc
                    ? edited
                    : choice.NarrationSourceChosenAtUtc!.Value;
                return new UserNarration(propertyId, acceptedAt, true, room.Segments);
            }
        }

        var script = await dbContext.PropertyNarrationScripts
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.MarketingUseAccepted)
            .Select(candidate => new { candidate.Id, candidate.ExtractedText, candidate.MarketingUseAcceptedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        return script is null
            ? null
            : new UserNarration(
                script.Id,
                script.MarketingUseAcceptedAtUtc!.Value,
                false,
                NarrationScriptPolicy.SplitIntoSegments(script.ExtractedText)
                    .Select((text, index) => new AcceptedNarrationScriptSegment(
                        $"acceptedScript.segment.{index + 1}",
                        text))
                    .ToArray());
    }

    /// <summary>The room narration for the photographed stops, in walking order, whether or not it is in use.</summary>
    public static async Task<(IReadOnlyList<AcceptedNarrationScriptSegment> Segments, DateTimeOffset? LatestEditUtc)>
        LoadRoomNarrationAsync(
            ApplicationDbContext dbContext,
            Guid organizationId,
            Guid propertyId,
            CancellationToken cancellationToken)
    {
        var narrations = await dbContext.WalkthroughStopNarrations
            .AsNoTracking()
            .Where(narration => narration.OrganizationId == organizationId && narration.PropertyId == propertyId)
            .Select(narration => new { narration.Stop, narration.Text, narration.UpdatedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (narrations.Length == 0)
        {
            return ([], null);
        }

        var storyboard = await dbContext.PropertyMedia
            .AsNoTracking()
            .Where(media => media.OrganizationId == organizationId
                && media.PropertyId == propertyId
                && !media.IsInGallery
                && media.WalkthroughStop != null)
            .OrderBy(media => media.DisplayOrder)
            .ThenBy(media => media.Id)
            .Select(media => new { media.WalkthroughStop, media.DisplayOrder })
            .ToArrayAsync(cancellationToken);
        var stops = RoomNarrationScript.StopsInWalkingOrder(storyboard, media => media.WalkthroughStop, media => media.DisplayOrder);
        var segments = RoomNarrationScript.Build(stops, narrations.Select(narration => (narration.Stop, narration.Text)));
        return (segments, narrations.Max(narration => narration.UpdatedAtUtc));
    }
}
