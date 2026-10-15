using ListingStudio.Application.Neighborhoods;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class NeighborhoodDistancePhrasingTests
{
    [Theory]
    [InlineData("0", "less than a mile away")]
    [InlineData("0.54", "less than a mile away")]
    [InlineData("0.99", "less than a mile away")]
    [InlineData("1", "about 1 mile away")]
    [InlineData("1.49", "about 1 mile away")]
    [InlineData("1.5", "about 2 miles away")]
    [InlineData("2.3", "about 2 miles away")]
    [InlineData("2.5", "about 3 miles away")]
    [InlineData("12.7", "about 13 miles away")]
    public void DescribesDistanceRoundedHalfUpToWholeMiles(string distanceMiles, string expected)
    {
        var distance = decimal.Parse(distanceMiles, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, NeighborhoodDistancePhrasing.Describe(distance));
    }
}
