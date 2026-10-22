using ListingStudio.Video.Processing;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VideoClipSamplingTests
{
    [Fact]
    public void SteadyDetailedFootageBecomesSpansOfTwoToFourSeconds()
    {
        // Ten seconds of a steady walk.
        var samples = Enumerable.Range(0, 20).Select(_ => new FrameSample(500, 20, 120)).ToArray();

        var spans = VideoClipSampling.FindSpans(samples);

        Assert.Equal([(500, 3_500), (3_500, 6_500), (6_500, 9_500)], spans.Select(span => (span.StartMs, span.EndMs)));
        Assert.All(spans, span => Assert.InRange(span.EndMs - span.StartMs, 2_000, 4_000));
    }

    [Fact]
    public void WhipPansBlurBlankWallsAndDarknessSplitTheFootage()
    {
        var samples = Enumerable.Range(0, 40).Select(_ => new FrameSample(500, 20, 120)).ToArray();
        samples[8] = samples[8] with { Difference = 60 };   // whip pan
        samples[16] = samples[16] with { Sharpness = 150 }; // motion blur or a blank wall
        samples[24] = samples[24] with { Brightness = 5 };  // lens covered

        var spans = VideoClipSampling.FindSpans(samples);

        Assert.Equal(
            [(500, 4_000), (4_500, 8_000), (8_500, 12_000), (12_500, 16_000), (16_000, 19_500)],
            spans.Select(span => (span.StartMs, span.EndMs)));
    }

    [Fact]
    public void ShortGlimpsesAreNotShots()
    {
        var samples = Enumerable.Range(0, 12).Select(index => new FrameSample(500, index % 3 == 0 ? 80 : 20, 120)).ToArray();

        Assert.Empty(VideoClipSampling.FindSpans(samples));
    }

    [Fact]
    public void DetailedViewsScoreHigherThanPlainOnes()
    {
        var samples = Enumerable.Range(0, 24)
            .Select(index => new FrameSample(index < 12 ? 700 : 500, 20, 120))
            .ToArray();

        var spans = VideoClipSampling.FindSpans(samples);

        Assert.True(spans[0].Score > spans[^1].Score);
    }
}
