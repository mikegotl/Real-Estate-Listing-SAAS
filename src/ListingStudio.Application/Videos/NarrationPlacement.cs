using ListingStudio.Application.Audio;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public sealed record PlacedNarrationSegment(string SegmentId, int StartMs, int DurationMs);

/// <summary>
/// Where each narration segment is actually heard. A segment starts with the scene it was planned against; one that
/// runs longer than its slot pushes the next one back, and the program end cuts it off. The renderer places the audio
/// this way, so captions and music ducking use the same windows instead of the planned ones.
/// </summary>
public static class NarrationPlacement
{
    public static IReadOnlyList<PlacedNarrationSegment> Place(
        IReadOnlyList<NarrationSegment> segments,
        VoiceTimingMetadata? timing,
        int programMs)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ordered = segments.OrderBy(segment => segment.StartMs);
        if (timing is null)
        {
            return ordered
                .Select(segment => new PlacedNarrationSegment(segment.Id, segment.StartMs, segment.DurationMs))
                .ToArray();
        }

        var spoken = timing.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var result = new List<PlacedNarrationSegment>(segments.Count);
        var previousEndMs = 0;
        foreach (var segment in ordered)
        {
            if (!spoken.TryGetValue(segment.Id, out var audio))
            {
                throw new ArgumentException($"Narration timing is missing segment {segment.Id}.", nameof(timing));
            }

            var startMs = Math.Max(segment.StartMs, previousEndMs);
            previousEndMs = startMs + audio.EndMs - audio.StartMs;
            var durationMs = Math.Min(previousEndMs, programMs) - startMs;
            if (durationMs > 0)
            {
                result.Add(new PlacedNarrationSegment(segment.Id, startMs, durationMs));
            }
        }

        return result;
    }
}
