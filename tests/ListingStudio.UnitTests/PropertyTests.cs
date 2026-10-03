using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyTests
{
    [Fact]
    public void CreateAssignsOrganizationAndNormalizesText()
    {
        var organizationId = Guid.NewGuid();

        var property = ListingProperty.Create(organizationId, ValidDetails with
        {
            Address1 = "  123 Main Street  ",
            Address2 = "  Unit 4  ",
            Description = "  Updated home  ",
        });

        Assert.Equal(organizationId, property.OrganizationId);
        Assert.Equal("123 Main Street", property.Address1);
        Assert.Equal("Unit 4", property.Address2);
        Assert.Equal("Updated home", property.Description);
        Assert.False(property.IsArchived);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void CreateRejectsNegativeListingPrice(decimal listingPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ListingProperty.Create(Guid.NewGuid(), ValidDetails with { ListingPrice = listingPrice }));
    }

    [Fact]
    public void UpdateChangesEditableDetails()
    {
        var property = ListingProperty.Create(Guid.NewGuid(), ValidDetails);

        property.Update(ValidDetails with { ListingStatus = ListingStatus.Active, Bedrooms = 4 });

        Assert.Equal(ListingStatus.Active, property.ListingStatus);
        Assert.Equal(4, property.Bedrooms);
    }

    [Fact]
    public void ArchiveMakesPropertyReadOnly()
    {
        var property = ListingProperty.Create(Guid.NewGuid(), ValidDetails);

        property.Archive();

        Assert.True(property.IsArchived);
        Assert.NotNull(property.ArchivedAtUtc);
        Assert.Throws<InvalidOperationException>(() => property.Update(ValidDetails));
    }

    [Fact]
    public void CreateRejectsInvalidOrganization()
    {
        Assert.Throws<ArgumentException>(() => ListingProperty.Create(Guid.Empty, ValidDetails));
    }

    private static PropertyDetails ValidDetails => new(
        "123 Main Street",
        null,
        "Raleigh",
        "NC",
        "27601",
        450_000m,
        3,
        2.5m,
        2_100,
        0.25m,
        1998,
        PropertyType.SingleFamily,
        "A comfortable home.",
        ListingStatus.Draft);
}
