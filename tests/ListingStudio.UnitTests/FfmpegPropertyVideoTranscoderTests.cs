using ListingStudio.Video.Processing;
using ListingStudio.Video.Rendering;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class FfmpegPropertyVideoTranscoderTests
{
    [Theory]
    [InlineData(300, 60, 300)]
    [InlineData(300, 736, 2_268)]
    [InlineData(300, 900, 2_760)]
    public void RenderTimeoutScalesForLongFormPrograms(
        int configuredMinimum,
        int duration,
        int expected)
    {
        Assert.Equal(
            expected,
            FfmpegVideoRenderer.CalculateRenderTimeoutSeconds(configuredMinimum, duration));
    }

    [Fact]
    public void EnhancementCommandStabilizesCadenceBrightnessAndColor()
    {
        var arguments = FfmpegPropertyVideoTranscoder.BuildEnhancementArguments("input.mov", "output.mp4").ToList();
        var filters = arguments[arguments.IndexOf("-vf") + 1];

        Assert.Contains("vidstabtransform=", filters, StringComparison.Ordinal);
        Assert.Contains("smoothing=30", filters, StringComparison.Ordinal);
        Assert.Contains("deflicker=", filters, StringComparison.Ordinal);
        Assert.Contains("grayworld", filters, StringComparison.Ordinal);
        Assert.Contains("normalize=", filters, StringComparison.Ordinal);
        Assert.Contains("eq=contrast=1.02:saturation=1.03", filters, StringComparison.Ordinal);
        Assert.Contains("fps=30", filters, StringComparison.Ordinal);
        Assert.Equal("libx264", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.Contains("+faststart", arguments);
    }
}
