using ListingStudio.Application.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PhotoQualityRatingTests
{
    [Theory]
    [InlineData(100, PhotoQuality.Great)]
    [InlineData(75, PhotoQuality.Great)]
    [InlineData(74, PhotoQuality.Ok)]
    [InlineData(50, PhotoQuality.Ok)]
    [InlineData(49, PhotoQuality.Retake)]
    [InlineData(0, PhotoQuality.Retake)]
    public void ScoresFallIntoThreeLevels(int score, PhotoQuality expected) =>
        Assert.Equal(expected, PhotoQualityRating.Rate(score));
}
