using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PresenterPortraitFramingTests
{
    private static PhotoPixels Photo(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                rgb[offset] = (byte)x;
                rgb[offset + 1] = (byte)y;
            }
        }

        return new PhotoPixels(width, height, rgb);
    }

    [Fact]
    public void AThreeByFourHeadshotLosesEqualPartsOfBothSides()
    {
        // 120 x 160 is 3:4; 9:16 at the same height is 90 wide, so 15 columns go from each side.
        var framed = PresenterPortraitFraming.CropToVertical(Photo(120, 160));

        Assert.Equal(90, framed.Width);
        Assert.Equal(160, framed.Height);
        Assert.Equal(15, framed.Rgb[0]);
        Assert.Equal(0, framed.Rgb[1]);
    }

    [Fact]
    public void ATallerPhotoIsCutMostlyFromTheBottomToKeepTheFace()
    {
        // 90 x 200 is taller than 9:16 (90 x 160); 40 rows go, 10 from the top and 30 from the bottom.
        var framed = PresenterPortraitFraming.CropToVertical(Photo(90, 200));

        Assert.Equal(90, framed.Width);
        Assert.Equal(160, framed.Height);
        Assert.Equal(10, framed.Rgb[1]);
    }

    [Fact]
    public void AVerticalPhotoIsLeftAlone()
    {
        var photo = Photo(90, 160);

        Assert.Same(photo, PresenterPortraitFraming.CropToVertical(photo));
    }
}
