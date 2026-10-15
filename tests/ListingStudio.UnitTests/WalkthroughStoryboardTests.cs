using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class WalkthroughStoryboardTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("exterior FRONT", WalkthroughStoryboard.ExteriorFront)]
    [InlineData("  entry / foyer ", WalkthroughStoryboard.EntryFoyer)]
    [InlineData("primary   bedroom", "Primary bedroom")]
    [InlineData("  Wine  cellar ", "Wine cellar")]
    public void NormalizeTrimsAndMatchesKnownStops(string? input, string? expected) =>
        Assert.Equal(expected, WalkthroughStoryboard.Normalize(input));

    [Fact]
    public void NormalizeRejectsStopsLongerThanTheColumn() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WalkthroughStoryboard.Normalize(
            new string('x', PropertyMedia.MaximumWalkthroughStopLength + 1)));

    [Fact]
    public void GroupPutsTemplateStopsFirstThenStopsByFirstPhotoThenUnsorted()
    {
        Photo[] photos =
        [
            new("legacy", null, 0),
            new("kitchen", "Kitchen", 1),
            new("foyer", WalkthroughStoryboard.EntryFoyer, 2),
            new("living", "Living room", 3),
            new("front", WalkthroughStoryboard.ExteriorFront, 4),
            new("kitchen-2", "kitchen", 5),
            new("legacy-2", null, 6),
        ];

        var stops = WalkthroughStoryboard.Group(photos, photo => photo.Stop, photo => photo.Order);

        Assert.Equal(
            [WalkthroughStoryboard.ExteriorFront, WalkthroughStoryboard.EntryFoyer, "Kitchen", "Living room", null],
            stops.Select(stop => stop.Stop));
        Assert.Equal(["kitchen", "kitchen-2"], stops[2].Items.Select(photo => photo.Name));
        Assert.Equal(
            ["front", "foyer", "kitchen", "kitchen-2", "living", "legacy", "legacy-2"],
            WalkthroughStoryboard.Arrange(photos, photo => photo.Stop, photo => photo.Order).Select(photo => photo.Name));
    }

    [Fact]
    public void ArrangeLeavesPhotosWithoutStopsInTheirExistingOrder()
    {
        Photo[] photos = [new("c", null, 2), new("a", null, 0), new("b", null, 1)];

        var stops = WalkthroughStoryboard.Group(photos, photo => photo.Stop, photo => photo.Order);

        var unsorted = Assert.Single(stops);
        Assert.Null(unsorted.Stop);
        Assert.Equal(["a", "b", "c"], unsorted.Items.Select(photo => photo.Name));
    }

    [Theory]
    [InlineData(PropertyMediaCategory.FrontExterior, WalkthroughStoryboard.ExteriorFront)]
    [InlineData(PropertyMediaCategory.Entry, WalkthroughStoryboard.EntryFoyer)]
    [InlineData(PropertyMediaCategory.Kitchen, "Kitchen")]
    [InlineData(PropertyMediaCategory.Patio, "Patio / deck")]
    [InlineData(PropertyMediaCategory.Other, null)]
    [InlineData(PropertyMediaCategory.Community, null)]
    public void SuggestStopMapsAnalysisCategories(PropertyMediaCategory category, string? expected) =>
        Assert.Equal(expected, WalkthroughStoryboard.SuggestStop(category));

    [Fact]
    public void EverySuggestedStopIsAKnownStopName()
    {
        foreach (var category in Enum.GetValues<PropertyMediaCategory>())
        {
            var suggestion = WalkthroughStoryboard.SuggestStop(category);
            if (suggestion is not null)
            {
                Assert.Contains(
                    suggestion,
                    WalkthroughStoryboard.TemplateStops.Concat(WalkthroughStoryboard.SuggestedStops));
            }
        }
    }

    [Fact]
    public void ShotPairsLinkEachShotToTheNextInStoryboardOrder()
    {
        Photo[] photos =
        [
            new("kitchen", "Kitchen", 0),
            new("legacy", null, 1),
            new("front", WalkthroughStoryboard.ExteriorFront, 2),
            new("foyer", WalkthroughStoryboard.EntryFoyer, 3),
        ];

        var pairs = WalkthroughStoryboard.ShotPairs(photos, photo => photo.Stop, photo => photo.Order);

        Assert.Equal(
            [("front", "foyer"), ("foyer", "kitchen"), ("kitchen", "legacy")],
            pairs.Select(pair => (pair.From.Name, pair.To.Name)));
    }

    [Fact]
    public void ShotPairsNeedAtLeastTwoShots()
    {
        Assert.Empty(WalkthroughStoryboard.ShotPairs(
            [new Photo("front", WalkthroughStoryboard.ExteriorFront, 0)],
            photo => photo.Stop,
            photo => photo.Order));
    }

    private sealed record Photo(string Name, string? Stop, int Order);
}
