namespace ListingStudio.Video.Processing;

/// <summary>
/// Chooses still candidates from low-resolution grayscale samples of a video. Hard cuts and long stretches split the
/// video into views; each view offers its sharpest well-lit sample, so a slow pan through a room yields a few
/// distinct stills instead of dozens of near-duplicates.
/// </summary>
public static class VideoFrameSampling
{
    public const int SamplesPerSecond = 2;
    public const int SampleWidth = 320;
    public const int SampleHeight = 180;

    /// <summary>
    /// A cut is a change between samples half a second apart (mean brightness change, 0 to 255) of at least this
    /// much that also stands out against the changes around it. Panning or walking changes every sample a lot, so
    /// only a spike above that steady motion counts.
    /// </summary>
    public const double MinimumCutDifference = 12;

    /// <summary>How many times the typical nearby change a cut must be.</summary>
    public const double CutContrast = 2.5;

    private const int CutNeighbourhood = 4;

    /// <summary>Samples darker than this on average (fades, a covered lens) never become stills.</summary>
    public const double MinimumBrightness = 18;

    /// <summary>A continuous shot is split into views of at most four seconds.</summary>
    public const int MaximumViewSamples = 4 * SamplesPerSecond;

    /// <summary>Views shorter than a second are passing glimpses, kept only if nothing longer exists.</summary>
    public const int MinimumViewSamples = SamplesPerSecond;

    public static FrameSample Measure(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> previous, int width, int height)
    {
        if (width < 3 || height < 3 || frame.Length < width * height)
        {
            throw new ArgumentException("The sample is smaller than its dimensions.", nameof(frame));
        }

        double brightness = 0;
        for (var index = 0; index < width * height; index++)
        {
            brightness += frame[index];
        }

        brightness /= width * height;

        // Variance of the Laplacian: crisp edges give strong second derivatives, motion blur flattens them.
        double sum = 0;
        double sumOfSquares = 0;
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var at = (y * width) + x;
                double laplacian = (4 * frame[at]) - frame[at - 1] - frame[at + 1] - frame[at - width] - frame[at + width];
                sum += laplacian;
                sumOfSquares += laplacian * laplacian;
                count++;
            }
        }

        var mean = sum / count;
        var sharpness = (sumOfSquares / count) - (mean * mean);

        double difference = 0;
        if (previous.Length >= width * height)
        {
            for (var index = 0; index < width * height; index++)
            {
                difference += Math.Abs(frame[index] - previous[index]);
            }

            difference /= width * height;
        }

        return new FrameSample(sharpness, difference, brightness);
    }

    /// <summary>Sample indices to turn into stills, in time order, at most <paramref name="maximumCandidates"/>.</summary>
    public static IReadOnlyList<int> ChooseFrames(IReadOnlyList<FrameSample> samples, int maximumCandidates)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0 || maximumCandidates <= 0)
        {
            return [];
        }

        var shots = new List<View>();
        var start = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            if (IsCut(samples, index))
            {
                shots.Add(new View(start, index));
                start = index;
            }
        }

        shots.Add(new View(start, samples.Count));
        var views = shots.SelectMany(Split).ToList();
        var lasting = views.Where(view => view.Length >= MinimumViewSamples).ToList();
        if (lasting.Count > 0)
        {
            views = lasting;
        }

        // Too many views for the budget: join the neighbours that together cover the least time.
        while (views.Count > maximumCandidates)
        {
            var join = Enumerable.Range(0, views.Count - 1)
                .MinBy(index => views[index + 1].End - views[index].Start);
            views[join] = new View(views[join].Start, views[join + 1].End);
            views.RemoveAt(join + 1);
        }

        return views
            .Select(view => Sharpest(samples, view))
            .OfType<int>()
            .ToArray();
    }

    private static bool IsCut(IReadOnlyList<FrameSample> samples, int index)
    {
        var difference = samples[index].Difference;
        if (difference < MinimumCutDifference)
        {
            return false;
        }

        var nearby = Enumerable.Range(index - CutNeighbourhood, (2 * CutNeighbourhood) + 1)
            .Where(neighbour => neighbour >= 1 && neighbour < samples.Count && neighbour != index)
            .Select(neighbour => samples[neighbour].Difference)
            .Order()
            .ToArray();
        var typical = nearby.Length == 0 ? 0 : nearby[nearby.Length / 2];
        return difference >= typical * CutContrast;
    }

    private static IEnumerable<View> Split(View shot)
    {
        var parts = (int)Math.Ceiling(shot.Length / (double)MaximumViewSamples);
        for (var part = 0; part < parts; part++)
        {
            yield return new View(
                shot.Start + (shot.Length * part / parts),
                shot.Start + (shot.Length * (part + 1) / parts));
        }
    }

    // The first and last samples of a view straddle the camera moving on, so they are skipped when there is a choice.
    private static int? Sharpest(IReadOnlyList<FrameSample> samples, View view)
    {
        var (first, last) = view.Length >= 3 ? (view.Start + 1, view.End - 1) : (view.Start, view.End);
        int? best = null;
        for (var index = first; index < last; index++)
        {
            if (samples[index].Brightness >= MinimumBrightness
                && (best is null || samples[index].Sharpness > samples[best.Value].Sharpness))
            {
                best = index;
            }
        }

        return best;
    }

    private sealed record View(int Start, int End)
    {
        public int Length => End - Start;
    }
}

/// <summary>One grayscale sample: Laplacian variance, mean change from the previous sample, and mean brightness.</summary>
public sealed record FrameSample(double Sharpness, double Difference, double Brightness);
