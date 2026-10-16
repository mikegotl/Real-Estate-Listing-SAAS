using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// Turns recognized video stills into room photos. A walkthrough video moves room by room, so consecutive stills
/// recognized as the same room are one room; each room keeps its one or two best stills.
/// </summary>
public static class VideoRoomFramePicker
{
    public const int MaximumPhotosPerRoom = 2;

    // A second photo of a room must be a genuinely different view, not the neighbouring moment of the same pan.
    public const int MinimumSecondPhotoGapMs = 1_500;

    public const int MinimumSecondPhotoQuality = 40;

    public static IReadOnlyList<VideoRoom> Pick(IReadOnlyList<AnalyzedVideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return [];
        }

        var ordered = frames.OrderBy(frame => frame.TimestampMs).ToArray();
        var sharpnessRank = SharpnessPercentiles(ordered);
        var runs = new List<List<AnalyzedVideoFrame>>();
        foreach (var frame in ordered)
        {
            if (runs.Count > 0 && RoomKey(runs[^1][0]) == RoomKey(frame))
            {
                runs[^1].Add(frame);
            }
            else
            {
                runs.Add([frame]);
            }
        }

        MergeFlickers(runs);
        return runs
            .Select(run => Best(run, sharpnessRank))
            .Select(ranked => new VideoRoom(ranked[0].Analysis.RoomType, ranked[0].Analysis.Category, Choose(ranked)))
            .ToArray();
    }

    /// <summary>Recognized rooms match on category, or on the named room type when the category is Other.</summary>
    public static string RoomKey(AnalyzedVideoFrame frame) => frame.Analysis.Category == PropertyMediaCategory.Other
        ? $"other:{frame.Analysis.RoomType.Trim().ToLowerInvariant()}"
        : frame.Analysis.Category.ToString();

    // A single still that disagrees with the room on both sides of it is a recognition wobble, not a room, so it is
    // dropped and the room around it joined back together.
    private static void MergeFlickers(List<List<AnalyzedVideoFrame>> runs)
    {
        for (var index = 1; index < runs.Count - 1; index++)
        {
            if (runs[index].Count == 1 && RoomKey(runs[index - 1][0]) == RoomKey(runs[index + 1][0]))
            {
                runs[index - 1].AddRange(runs[index + 1]);
                runs.RemoveRange(index, 2);
                index--;
            }
        }
    }

    // Stills without people first, then by the AI's photo quality blended with how sharp the still is compared
    // with the rest of the video.
    private static AnalyzedVideoFrame[] Best(
        IReadOnlyList<AnalyzedVideoFrame> run,
        Dictionary<AnalyzedVideoFrame, double> sharpnessRank) => run
        .OrderBy(frame => frame.Analysis.ContainsPeople)
        .ThenByDescending(frame => (frame.Analysis.QualityScore * 0.6) + (sharpnessRank[frame] * 40))
        .ThenBy(frame => frame.TimestampMs)
        .ToArray();

    private static IReadOnlyList<AnalyzedVideoFrame> Choose(AnalyzedVideoFrame[] ranked)
    {
        var picks = new List<AnalyzedVideoFrame> { ranked[0] };
        var second = ranked.Skip(1).FirstOrDefault(frame =>
            Math.Abs(frame.TimestampMs - ranked[0].TimestampMs) >= MinimumSecondPhotoGapMs
            && frame.Analysis.QualityScore >= MinimumSecondPhotoQuality
            && (!frame.Analysis.ContainsPeople || ranked[0].Analysis.ContainsPeople));
        if (second is not null)
        {
            picks.Add(second);
        }

        return [.. picks.OrderBy(frame => frame.TimestampMs)];
    }

    // 0 for the softest still in the video, 1 for the sharpest; equally sharp stills share a rank.
    private static Dictionary<AnalyzedVideoFrame, double> SharpnessPercentiles(AnalyzedVideoFrame[] frames)
    {
        var ranks = new Dictionary<AnalyzedVideoFrame, double>(ReferenceEqualityComparer.Instance);
        foreach (var frame in frames)
        {
            var softer = frames.Count(other => other.Sharpness < frame.Sharpness);
            var ties = frames.Count(other => other.Sharpness == frame.Sharpness) - 1;
            ranks[frame] = frames.Length == 1 ? 1 : (softer + (ties / 2d)) / (frames.Length - 1);
        }

        return ranks;
    }
}

public sealed record AnalyzedVideoFrame(int TimestampMs, double Sharpness, PropertyMediaAnalysis Analysis);

public sealed record VideoRoom(string RoomType, PropertyMediaCategory Category, IReadOnlyList<AnalyzedVideoFrame> Photos);
