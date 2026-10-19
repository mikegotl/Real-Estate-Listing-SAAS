using ListingStudio.Application.Stories;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class WalkthroughStoryOutlineTests
{
    [Fact]
    public void FollowsStoryboardStopOrderAndGroupsEachStopsPhotos()
    {
        var front = Photo("Exterior front", 2, "Front of a two-story home.");
        var kitchenWide = Photo("Kitchen", 0, "Large, bright kitchen with a big island.");
        var foyer = Photo("Entry / foyer", 3, "Open entry with a staircase.");
        var kitchenDetail = Photo("kitchen", 4, "Island seating under pendant lights.");
        var unplaced = Photo(null, 1, "Closet interior.");

        var outline = WalkthroughStoryOutline.Build(
            [front, kitchenWide, foyer, kitchenDetail, unplaced],
            new Dictionary<string, WalkthroughDirection>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["Exterior front", "Entry / foyer", "Kitchen"], outline.Select(stop => stop.Stop));
        Assert.Equal([1, 2, 3], outline.Select(stop => stop.Order));
        Assert.Equal(
            [kitchenWide.MediaId, kitchenDetail.MediaId],
            outline[2].Photos.Select(photo => photo.MediaId));
        Assert.DoesNotContain(outline.SelectMany(stop => stop.Photos), photo => photo.MediaId == unplaced.MediaId);
    }

    [Fact]
    public void GivesOnlyDirectionsTheAgentSetAndTracksLevels()
    {
        var outline = WalkthroughStoryOutline.Build(
            [
                Photo("Exterior front", 0, "Front."),
                Photo("Entry / foyer", 1, "Entry."),
                Photo("Living room", 2, "Living room."),
                Photo("Primary bedroom", 3, "Bedroom."),
                Photo("Backyard", 4, "Backyard."),
            ],
            new Dictionary<string, WalkthroughDirection>(StringComparer.OrdinalIgnoreCase)
            {
                ["living room"] = WalkthroughDirection.Left,
                ["Primary bedroom"] = WalkthroughDirection.Upstairs,
            });

        Assert.Equal(
            [null, null, "turn left", "up the stairs", null],
            outline.Select(stop => stop.WayFromPreviousStop));
        // The backyard's way back down is assumed by the map, so the story is not told it, but it is on the main level.
        Assert.Equal(
            ["Main level", "Main level", "Main level", "Upper level", "Main level"],
            outline.Select(stop => stop.Level));
    }

    [Fact]
    public void PropertyWithoutStopsHasNoOutline()
    {
        var outline = WalkthroughStoryOutline.Build(
            [Photo(null, 0, "Front."), Photo(null, 1, "Kitchen.")],
            new Dictionary<string, WalkthroughDirection>());

        Assert.Empty(outline);
    }

    private static WalkthroughStoryPhoto Photo(string? stop, int order, string description) =>
        new(Guid.NewGuid(), stop, order, stop ?? "Other", description);
}
