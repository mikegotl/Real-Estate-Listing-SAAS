using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyMediaTests
{
    [Fact]
    public void CreateAssignsMetadataAndPendingAnalysis()
    {
        var organizationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var media = PropertyMedia.Create(
            organizationId,
            propertyId,
            "organizations/org/properties/property/photo.png",
            "photo.png",
            "image/png",
            1_024,
            1_920,
            1_080,
            3);

        Assert.Equal(organizationId, media.OrganizationId);
        Assert.Equal(propertyId, media.PropertyId);
        Assert.Equal(3, media.DisplayOrder);
        Assert.Equal(PropertyMediaAnalysisStatus.Pending, media.AnalysisStatus);
        Assert.NotEqual(Guid.Empty, media.Id);
    }

    [Fact]
    public void SetDisplayOrderRejectsNegativeValue()
    {
        var media = CreateMedia();

        Assert.Throws<ArgumentOutOfRangeException>(() => media.SetDisplayOrder(-1));
    }

    [Fact]
    public void CreateRejectsInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PropertyMedia.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "image.png",
            "image.png",
            "image/png",
            100,
            0,
            100,
            0));
    }

    private static PropertyMedia CreateMedia() => PropertyMedia.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "image.png",
        "image.png",
        "image/png",
        100,
        10,
        10,
        0);
}
