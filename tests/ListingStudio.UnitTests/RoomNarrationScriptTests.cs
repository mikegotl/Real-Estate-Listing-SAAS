using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class RoomNarrationScriptTests
{
    [Fact]
    public void FollowsWalkingOrderAndSkipsStopsWithoutTextOrPhotos()
    {
        var segments = RoomNarrationScript.Build(
            ["Exterior front", "Entry / foyer", "Kitchen", "Primary bedroom"],
            [
                ("kitchen", "The kitchen opens to the patio."),
                ("Exterior front", "Welcome home."),
                ("Garage", "Never shown in the video."),
                ("Entry / foyer", "   "),
            ]);

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal("roomNarration.segment.1", first.Key);
                Assert.Equal("Exterior front", first.WalkthroughStop);
                Assert.Equal("Welcome home.", first.Text);
            },
            second =>
            {
                Assert.Equal("roomNarration.segment.2", second.Key);
                Assert.Equal("Kitchen", second.WalkthroughStop);
            });
        Assert.Equal("Welcome home. The kitchen opens to the patio.", RoomNarrationScript.FullText(segments));
    }

    [Fact]
    public void SplitsALongPassageIntoNumberedSegmentsForTheSameStop()
    {
        var sentence = "The living room has tall windows and a warm wood floor that runs the length of the house.";
        var text = string.Join(' ', Enumerable.Repeat(sentence, 6));

        var segments = RoomNarrationScript.Build(["Living room"], [("Living room", text)]);

        Assert.True(segments.Count > 1);
        Assert.All(segments, segment => Assert.Equal("Living room", segment.WalkthroughStop));
        Assert.Equal(
            Enumerable.Range(1, segments.Count).Select(index => $"roomNarration.segment.{index}"),
            segments.Select(segment => segment.Key));
        Assert.Equal(text, RoomNarrationScript.FullText(segments));
    }

    [Fact]
    public void WalkingOrderPutsTemplateStopsFirstAndIgnoresUnplacedPhotos()
    {
        var media = new (string? Stop, int Order)[]
        {
            ("Kitchen", 0),
            ("Exterior front", 1),
            (null, 2),
            ("Kitchen", 3),
            ("Dining room", 4),
        };

        var stops = RoomNarrationScript.StopsInWalkingOrder(media, item => item.Stop, item => item.Order);

        Assert.Equal(["Exterior front", "Kitchen", "Dining room"], stops);
    }

    [Fact]
    public void NarrationTextIsTrimmedCollapsedAndLimited()
    {
        var narration = WalkthroughStopNarration.Create(
            Guid.NewGuid(), Guid.NewGuid(), " Kitchen ", "  Bright\n\n and   open.  ", "user-1", DateTimeOffset.UtcNow);

        Assert.Equal("Kitchen", narration.Stop);
        Assert.Equal("Bright and open.", narration.Text);
        Assert.Throws<ArgumentException>(() => narration.SetText(" ", "user-1", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => narration.SetText(
            new string('a', WalkthroughStopNarration.MaximumTextLength + 1), "user-1", DateTimeOffset.UtcNow));
    }
}
