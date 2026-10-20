using ListingStudio.Application.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ZillowListingLinkTests
{
    [Fact]
    public void ParseNormalizesCanonicalLinkAndRemovesTrackingValues()
    {
        var result = ZillowListingLink.Parse(
            " https://zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/987654321_zpid/?utm_campaign=share#photos ");

        Assert.Equal(
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/987654321_zpid/",
            result.Url);
        Assert.Equal("987654321", result.ZillowPropertyId);
    }

    [Theory]
    [InlineData("http://www.zillow.com/homedetails/a/123_zpid/")]
    [InlineData("https://evil.zillow.com/homedetails/a/123_zpid/")]
    [InlineData("https://www.zillow.com/profile/example")]
    [InlineData("https://www.zillow.com/homedetails/123-main-st/")]
    [InlineData("https://example.com/homedetails/a/123_zpid/")]
    public void ParseRejectsLinksThatAreNotSecureCanonicalHomeDetailsPages(string value)
    {
        Assert.Throws<ArgumentException>(() => ZillowListingLink.Parse(value));
    }

    [Theory]
    [InlineData("123 Main Street", "27601", true)]
    [InlineData("123 Main Street", "27602", false)]
    [InlineData("124 Main Street", "27601", false)]
    [InlineData("123 Oak Street", "27601", false)]
    public void AppearsToMatchRequiresStreetNumberNameAndZip(
        string address1,
        string zipCode,
        bool expected)
    {
        var link = ZillowListingLink.Parse(
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/987654321_zpid/");

        Assert.Equal(expected, link.AppearsToMatch(address1, zipCode));
    }
}
