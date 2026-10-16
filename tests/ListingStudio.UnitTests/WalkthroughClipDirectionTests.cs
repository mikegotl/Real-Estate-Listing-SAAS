using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class WalkthroughClipDirectionTests
{
    private static readonly Dictionary<string, WalkthroughDirection> Directions =
        new(StringComparer.OrdinalIgnoreCase) { ["Kitchen"] = WalkthroughDirection.Right };

    [Theory]
    [InlineData("Living room", "kitchen", WalkthroughDirection.Right)]
    [InlineData("Kitchen", "Kitchen", null)]
    [InlineData("Kitchen", "kitchen", null)]
    [InlineData("Kitchen", "Pantry", null)]
    [InlineData("Kitchen", null, null)]
    [InlineData(null, "Kitchen", WalkthroughDirection.Right)]
    public void ClipsTakeTheNextStopsDirectionOnlyWhenCrossingIntoIt(
        string? from,
        string? to,
        WalkthroughDirection? expected) =>
        Assert.Equal(expected, WalkthroughClipDirection.ForPair(from, to, Directions));

    [Fact]
    public void DirectionRoundTripsThroughTheGenerationVersion()
    {
        Assert.Equal("higgsfield/v1/abc", WalkthroughClipDirection.Version("higgsfield/v1/abc", null));
        Assert.Null(WalkthroughClipDirection.FromVersion("higgsfield/v1/abc"));
        foreach (var direction in Enum.GetValues<WalkthroughDirection>())
        {
            var version = WalkthroughClipDirection.Version("higgsfield/v1/abc", direction);
            Assert.StartsWith("higgsfield/v1/abc/walk-", version, StringComparison.Ordinal);
            Assert.True(version.Length <= 100);
            Assert.Equal(direction, WalkthroughClipDirection.FromVersion(version));
        }

        Assert.Null(WalkthroughClipDirection.FromVersion("higgsfield/v1/abc/walk-sideways"));
    }
}
