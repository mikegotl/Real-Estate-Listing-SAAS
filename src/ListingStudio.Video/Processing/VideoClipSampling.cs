using ListingStudio.Application.Properties;

namespace ListingStudio.Video.Processing;

/// <summary>
/// Chooses short-form shots from low-resolution grayscale samples of the vertical (9:16) middle of a video. A sample
/// is usable when it is lit, not a whip pan or a cut, and about as detailed as the footage around it: a blank wall or
/// a motion-blurred frame is far softer than its neighbours. Runs of usable samples become spans of two to four
/// seconds, so a long walk through a room offers a few distinct shots.
/// </summary>
public static class VideoClipSampling
{
    public const int SamplesPerSecond = 2;
    public const int SampleWidth = 180;
    public const int SampleHeight = 320;

    /// <summary>Mean change between samples half a second apart above which the camera is whipping round.</summary>
    public const double MaximumDifference = 45;

    /// <summary>Detail at least this share of the footage around it (eight seconds either side).</summary>
    public const double MinimumLocalSharpness = 0.6;

    /// <summary>And at least this share of the whole video, so a long run of wall or ceiling never qualifies.</summary>
    public const double MinimumOverallSharpness = 0.25;

    public const int MinimumSpanSamples = 2 * SamplesPerSecond;
    public const int MaximumSpanSamples = 4 * SamplesPerSecond;
    private const int Neighbourhood = 8 * SamplesPerSecond;

    public static IReadOnlyList<VideoClipSpan> FindSpans(IReadOnlyList<FrameSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < MinimumSpanSamples)
        {
            return [];
        }

        var overall = Median(samples.Select(sample => sample.Sharpness));
        if (overall <= 0)
        {
            return [];
        }

        var detail = new double[samples.Count];
        var usable = new bool[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var local = Median(Enumerable.Range(
                    Math.Max(0, index - Neighbourhood),
                    Math.Min(samples.Count, index + Neighbourhood + 1) - Math.Max(0, index - Neighbourhood))
                .Select(neighbour => samples[neighbour].Sharpness));
            var sample = samples[index];
            detail[index] = sample.Sharpness / overall;
            usable[index] = sample.Brightness >= VideoFrameSampling.MinimumBrightness
                && sample.Difference <= MaximumDifference
                && sample.Sharpness >= MinimumOverallSharpness * overall
                && sample.Sharpness >= MinimumLocalSharpness * local;
        }

        // The first sample has nothing to compare with and the last may be the camera being lowered.
        usable[0] = false;
        usable[^1] = false;

        var spans = new List<VideoClipSpan>();
        var start = -1;
        for (var index = 0; index <= samples.Count; index++)
        {
            if (index < samples.Count && usable[index])
            {
                start = start < 0 ? index : start;
                continue;
            }

            if (start >= 0)
            {
                AddRun(start, index, detail, spans);
                start = -1;
            }
        }

        return spans;
    }

    private static void AddRun(int start, int end, double[] detail, List<VideoClipSpan> spans)
    {
        var length = end - start;
        if (length < MinimumSpanSamples)
        {
            return;
        }

        var parts = (int)Math.Ceiling(length / (double)MaximumSpanSamples);
        for (var part = 0; part < parts; part++)
        {
            var from = start + (length * part / parts);
            var to = start + (length * (part + 1) / parts);
            if (to - from < MinimumSpanSamples)
            {
                continue;
            }

            // Detail counts for most of the score, capped so one busy exterior does not outrank every room.
            var score = Enumerable.Range(from, to - from).Average(index => Math.Min(detail[index], 3));
            spans.Add(new VideoClipSpan(
                from * 1_000 / SamplesPerSecond,
                to * 1_000 / SamplesPerSecond,
                Math.Round(score, 3)));
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }
}
