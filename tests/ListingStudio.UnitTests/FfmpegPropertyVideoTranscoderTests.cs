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
        Assert.Contains("eq=brightness=0.03:contrast=1.05:saturation=1.08", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("zscale", filters, StringComparison.Ordinal);
        Assert.Contains("fps=30", filters, StringComparison.Ordinal);
        Assert.Equal("libx264", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void HdrPhoneVideoIsConvertedToStandardRangeBeforeTheBrightGrade()
    {
        var arguments = FfmpegPropertyVideoTranscoder.BuildEnhancementArguments("input.mov", "output.mp4", isHdr: true).ToList();
        var filters = arguments[arguments.IndexOf("-vf") + 1];

        Assert.StartsWith(HdrToneMapping.ToStandardRange + ",vidstabtransform=", filters, StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=hable", filters, StringComparison.Ordinal);
        Assert.Contains("eq=brightness=0.04:contrast=1.08:saturation=1.08:gamma=1.04", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("grayworld", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("normalize=", filters, StringComparison.Ordinal);
        Assert.EndsWith("fps=30,scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1", filters, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("arib-std-b67", true)]
    [InlineData("smpte2084", true)]
    [InlineData("bt709", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HlgAndPqAreHdr(string? transfer, bool expected) =>
        Assert.Equal(expected, HdrToneMapping.IsHdrTransfer(transfer));

    [Fact]
    public void RoomPhotosAndClipSamplesFromHdrVideoAreConvertedAfterShrinking()
    {
        var samples = FfmpegPropertyVideoFrameExtractor.BuildSamplingArguments("in.mov", "s.gray", isHdr: true);
        var still = FfmpegPropertyVideoFrameExtractor.BuildStillArguments("in.mov", "s.jpg", 1_000, isHdr: true);
        var clips = FfmpegPropertyVideoClipFinder.BuildSamplingArguments("in.mov", "c.gray", isHdr: true);
        var plain = FfmpegPropertyVideoClipFinder.BuildSamplingArguments("in.mov", "c.gray");

        Assert.Equal("fps=2,scale=320:180," + HdrToneMapping.ToStandardRange + ",format=gray", Filter(samples));
        Assert.StartsWith(HdrToneMapping.ToStandardRange + ",scale=", Filter(still), StringComparison.Ordinal);
        Assert.Equal(
            "fps=2,crop='min(iw,ih*9/16)':'min(ih,iw*16/9)',scale=180:320," + HdrToneMapping.ToStandardRange + ",format=gray",
            Filter(clips));
        Assert.DoesNotContain("zscale", Filter(plain), StringComparison.Ordinal);
    }

    private static string Filter(IReadOnlyList<string> arguments) => arguments[arguments.ToList().IndexOf("-vf") + 1];
}
