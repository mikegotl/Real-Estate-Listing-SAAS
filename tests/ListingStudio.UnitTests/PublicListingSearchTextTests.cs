using ListingStudio.Application.Leads;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PublicListingSearchTextTests
{
    [Fact]
    public void TitleNamesTheAddressCityBedsBathsAndPrice()
    {
        Assert.Equal(
            "12 Oak Lane, Austin, TX · 4 bd · 3.5 ba · $1,250,000",
            PublicListingSearchText.Title(Listing(description: null)));
    }

    [Fact]
    public void DescriptionFallsBackToAFactualSummary()
    {
        var withoutVideo = PublicListingSearchText.Description(Listing(description: "   "));
        var withVideo = PublicListingSearchText.Description(Listing(description: null, hasVideo: true));

        Assert.Equal(
            "4 bed, 3.5 bath home, 2,850 sq ft in Austin, TX, listed at $1,250,000. Contact the listing agent.",
            withoutVideo);
        Assert.EndsWith("Watch the tour and contact the listing agent.", withVideo, StringComparison.Ordinal);
    }

    [Fact]
    public void LongDescriptionIsCutAtAWordWithinTheLimit()
    {
        var description = string.Join(' ', Enumerable.Repeat("Sunlit rooms\nwith   oak floors,", 20));

        var result = PublicListingSearchText.Description(Listing(description));

        Assert.True(result.Length <= PublicListingSearchText.MaximumDescriptionLength);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain(",…", result, StringComparison.Ordinal);
    }

    private static PublicListingResult Listing(string? description, bool hasVideo = false) => new(
        "abc", "Oak Realty", "12 Oak Lane", null, "Austin", "TX", "78701", 1_250_000m,
        4, 3.5m, 2_850, 0.3m, 2004, "SingleFamily", description, null, HasCover: false, hasVideo);
}
