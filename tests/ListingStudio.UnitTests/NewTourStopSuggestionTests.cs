using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Web.Components.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class NewTourStopSuggestionTests
{
    [Fact]
    public void TaggedPhotoOffersItsRoomTagOnce()
    {
        var item = Item(new PropertyMediaAnalysis(
            PropertyMediaCategory.Kitchen, "Kitchen", 40, 30, false, true, false, [], "A kitchen.", 3));

        Assert.Equal(["Kitchen"], PropertyMediaManager.NewStopSuggestions(item));
    }

    [Fact]
    public void SpecificRoomTagComesBeforeTheSuggestedStop()
    {
        var item = Item(new PropertyMediaAnalysis(
            PropertyMediaCategory.Kitchen, "Breakfast nook", 40, 30, false, true, false, [], "A nook.", 3));

        var names = PropertyMediaManager.NewStopSuggestions(item);

        Assert.Equal("Breakfast nook", names[0]);
        Assert.Contains("Kitchen", names);
    }

    [Fact]
    public void PhotoWithoutATagOffersNothing()
    {
        Assert.Empty(PropertyMediaManager.NewStopSuggestions(Item(null)));
    }

    private static PropertyMediaItem Item(PropertyMediaAnalysis? analysis) => new(
        Guid.NewGuid(),
        "photo.jpg",
        "image/jpeg",
        1_024,
        1_920,
        1_080,
        0,
        DateTimeOffset.UtcNow,
        analysis is null ? PropertyMediaAnalysisStatus.Pending : PropertyMediaAnalysisStatus.Completed,
        analysis,
        analysis is null ? 0 : 1,
        null,
        IsInGallery: true);
}
