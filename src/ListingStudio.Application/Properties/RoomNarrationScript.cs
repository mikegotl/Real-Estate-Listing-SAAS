using ListingStudio.Application.Videos;

namespace ListingStudio.Application.Properties;

/// <summary>
/// The agent's room-by-room narration as one voiceover script: each photographed stop's text in walkthrough order,
/// tagged with its stop so the video can start that passage when the tour reaches the room.
/// </summary>
public static class RoomNarrationScript
{
    public const string KeyPrefix = "roomNarration.segment.";

    /// <summary>
    /// Builds the script from the storyboard stops in walking order. Stops without photos are left out, since the
    /// video never shows them; a long passage is split at sentences like an uploaded script.
    /// </summary>
    public static IReadOnlyList<AcceptedNarrationScriptSegment> Build(
        IEnumerable<string> stopsInWalkingOrder,
        IEnumerable<(string Stop, string Text)> narrations)
    {
        ArgumentNullException.ThrowIfNull(stopsInWalkingOrder);
        ArgumentNullException.ThrowIfNull(narrations);
        var byStop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stop, text) in narrations)
        {
            if (!string.IsNullOrWhiteSpace(stop) && !string.IsNullOrWhiteSpace(text))
            {
                byStop.TryAdd(stop.Trim(), text.Trim());
            }
        }

        var segments = new List<AcceptedNarrationScriptSegment>();
        foreach (var stop in stopsInWalkingOrder.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byStop.TryGetValue(stop, out var text))
            {
                continue;
            }

            foreach (var passage in NarrationScriptPolicy.SplitIntoSegments(text))
            {
                segments.Add(new AcceptedNarrationScriptSegment(
                    $"{KeyPrefix}{segments.Count + 1}",
                    passage,
                    stop));
            }
        }

        return segments;
    }

    /// <summary>The stops a photographed walkthrough visits, in walking order, from storyboard-ordered media.</summary>
    public static IReadOnlyList<string> StopsInWalkingOrder<T>(
        IEnumerable<T> storyboardMedia,
        Func<T, string?> stopSelector,
        Func<T, int> orderSelector) => WalkthroughStoryboard.Group(storyboardMedia, stopSelector, orderSelector)
        .Where(group => group.Stop is not null && group.Items.Count > 0)
        .Select(group => group.Stop!)
        .ToArray();

    public static string FullText(IReadOnlyList<AcceptedNarrationScriptSegment> segments) =>
        string.Join(' ', segments.Select(segment => segment.Text));
}
