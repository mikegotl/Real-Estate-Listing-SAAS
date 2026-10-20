using ListingStudio.Domain.Properties;
using System.Globalization;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyListingSourceTests
{
    [Fact]
    public void CreateRecordsTenantProviderAndAddressConfirmation()
    {
        var organizationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z", CultureInfo.InvariantCulture);

        var source = PropertyListingSource.Create(
            organizationId,
            propertyId,
            PropertyListingProvider.Zillow,
            "https://www.zillow.com/homedetails/a/123_zpid/",
            "123",
            "user-1",
            now);

        Assert.Equal(organizationId, source.OrganizationId);
        Assert.Equal(propertyId, source.PropertyId);
        Assert.Equal(PropertyListingProvider.Zillow, source.Provider);
        Assert.Equal("user-1", source.AddressConfirmedByUserId);
        Assert.Equal(now, source.AddressConfirmedAtUtc);
    }

    [Fact]
    public void UpdateReplacesTheLinkAndRequiresFreshConfirmation()
    {
        var source = PropertyListingSource.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PropertyListingProvider.Zillow,
            "https://www.zillow.com/homedetails/a/123_zpid/",
            "123",
            "user-1",
            DateTimeOffset.Parse("2026-10-20T12:00:00Z", CultureInfo.InvariantCulture));
        var changedAt = DateTimeOffset.Parse("2026-10-20T13:00:00Z", CultureInfo.InvariantCulture);

        source.Update(
            "https://www.zillow.com/homedetails/b/456_zpid/",
            "456",
            "user-2",
            changedAt);

        Assert.Equal("456", source.ExternalListingId);
        Assert.Equal("user-2", source.AddressConfirmedByUserId);
        Assert.Equal(changedAt, source.AddressConfirmedAtUtc);
        Assert.Equal(changedAt, source.UpdatedAtUtc);
    }
}
