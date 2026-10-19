using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Stories;

/// <summary>
/// The walkthrough the story's voiceover follows: the storyboard stops in walking order, each with the way the buyer
/// gets there from the stop before it on the walkthrough map and what that stop's photos show.
/// </summary>
public static class WalkthroughStoryOutline
{
    /// <summary>
    /// Builds the outline from storyboard photos. Photos without a stop are left out, because nobody has placed them
    /// in the walk. A direction is only given when the agent set it on the map; an assumed direction is left out so
    /// the story never states a layout the agent has not confirmed.
    /// </summary>
    public static IReadOnlyList<WalkthroughStoryStop> Build(
        IEnumerable<WalkthroughStoryPhoto> photos,
        IReadOnlyDictionary<string, WalkthroughDirection> stopDirections)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(stopDirections);
        var stops = WalkthroughStoryboard.Group(photos, photo => photo.Stop, photo => photo.DisplayOrder)
            .Where(stop => stop.Stop is not null)
            .ToArray();
        var map = WalkthroughMap.Build(stops
            .Select(stop => new WalkthroughMapStop(
                stop.Stop!,
                stopDirections.TryGetValue(stop.Stop!, out var direction) ? direction : null))
            .ToArray());
        return stops
            .Select((stop, index) =>
            {
                var space = map.Spaces[index];
                return new WalkthroughStoryStop(
                    index + 1,
                    stop.Stop!,
                    WalkthroughMap.LevelName(space.Level),
                    space.Direction is { } direction && !space.IsAssumed ? WalkthroughMap.Describe(direction) : null,
                    stop.Items
                        .Select(photo => new WalkthroughStoryShot(photo.MediaId, photo.RoomType, photo.Description))
                        .ToArray());
            })
            .ToArray();
    }
}

public sealed record WalkthroughStoryPhoto(
    Guid MediaId,
    string? Stop,
    int DisplayOrder,
    string RoomType,
    string Description);

/// <summary>
/// One stop in the walk. <see cref="WayFromPreviousStop"/> reads like "turn left" or "up the stairs" and is null for
/// the first stop and for stops whose direction the agent has not set.
/// </summary>
public sealed record WalkthroughStoryStop(
    int Order,
    string Stop,
    string Level,
    string? WayFromPreviousStop,
    IReadOnlyList<WalkthroughStoryShot> Photos);

public sealed record WalkthroughStoryShot(Guid MediaId, string RoomType, string Description);
