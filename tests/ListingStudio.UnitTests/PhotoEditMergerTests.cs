using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PhotoEditMergerTests
{
    private const int Width = 600;
    private const int Height = 400;

    // A shoe on the floor: the clutter the edit removes.
    private static readonly PhotoRegion Clutter = new(300, 300, 40, 30);

    [Fact]
    public void KeepsTheOriginalPhotoAndPastesInOnlyTheRemovedItem()
    {
        var original = Room(withClutter: true);
        // The model returns a smaller, slightly brighter and warmer redraw of the room with the shoe gone.
        var edited = Shift(Resize(Room(withClutter: false), Width / 2, Height / 2), red: 12, green: 6, blue: 0);

        var merge = PhotoEditMerger.Merge(original, edited);

        Assert.Equal((Width, Height), (merge.Photo.Width, merge.Photo.Height));
        Assert.InRange(merge.ChangedFraction, 0.001, 0.05);
        var tidy = Room(withClutter: false);
        // Where the shoe was, the floor behind it shows through.
        AssertClose(tidy, merge.Photo, Clutter.X + Clutter.Width / 2, Clutter.Y + Clutter.Height / 2, tolerance: 12);
        // Everywhere away from it the pixels are exactly the original's, with no change of tone or detail.
        foreach (var (x, y) in new[] { (10, 10), (150, 120), (590, 390), (450, 60), (100, 350) })
        {
            Assert.Equal(Pixel(original, x, y), Pixel(merge.Photo, x, y));
        }
    }

    [Fact]
    public void RejectsAnEditThatRedrewTheRoom()
    {
        var original = Room(withClutter: true);
        var otherRoom = Room(withClutter: false, seed: 7);

        var rejection = Assert.Throws<PhotoFixRejectedException>(() => PhotoEditMerger.Merge(original, otherRoom));

        Assert.Contains("changed the room itself", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnEditThatMovedTheCamera()
    {
        var original = Room(withClutter: false);
        var moved = Room(withClutter: false, offsetX: 40, offsetY: 25);

        Assert.Throws<PhotoFixRejectedException>(() => PhotoEditMerger.Merge(original, moved));
    }

    [Fact]
    public void RejectsAnEditThatRemovedNothing()
    {
        var original = Room(withClutter: false);

        var rejection = Assert.Throws<PhotoFixRejectedException>(
            () => PhotoEditMerger.Merge(original, Shift(original, red: 5, green: 5, blue: 5)));

        Assert.Contains("no clutter", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskedMergeKeepsEveryPixelOutsideTheApprovedArea()
    {
        var original = Room(withClutter: true);
        var edited = Room(withClutter: false, seed: 7);
        PhotoEditRegion[] regions = [new(.48m, .7m, .16m, .2m)];

        var merge = PhotoEditMerger.MergeMasked(original, edited, regions);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var normalizedX = (x + .5) / Width;
                var normalizedY = (y + .5) / Height;
                var inside = regions.Any(region =>
                    normalizedX >= (double)region.X
                    && normalizedX <= (double)(region.X + region.Width)
                    && normalizedY >= (double)region.Y
                    && normalizedY <= (double)(region.Y + region.Height));
                if (!inside)
                {
                    Assert.Equal(Pixel(original, x, y), Pixel(merge.Photo, x, y));
                }
            }
        }
    }

    [Fact]
    public void MaskedMergeLeavesNoSeamAtTheEdgeOfTheMarkedBox()
    {
        var original = Room(withClutter: true);
        // A half-size redraw with the shoe gone whose floor came back noticeably darker around the box.
        var tidy = Room(withClutter: false);
        var edited = Resize(Darken(tidy, new PhotoRegion(250, 270, 140, 90), amount: 30), Width / 2, Height / 2);
        // The agent drew a generous box around the shoe: x 260-380, y 280-350.
        PhotoEditRegion[] regions = [new(.4333m, .7m, .2m, .175m)];

        var merge = PhotoEditMerger.MergeMasked(original, edited, regions);

        // The shoe is gone, and the floor where it stood is the floor's color, not the redraw's darker one.
        AssertClose(tidy, merge.Photo, Clutter.X + Clutter.Width / 2, Clutter.Y + Clutter.Height / 2, tolerance: 14);
        // Parts of the box the edit did not change keep the uploaded pixels, so the box's outline never shows.
        foreach (var (x, y) in new[] { (275, 285), (275, 340), (368, 288), (368, 340) })
        {
            Assert.Equal(Pixel(original, x, y), Pixel(merge.Photo, x, y));
        }
    }

    [Fact]
    public void MaskedMergeBlendsAPatchIntoTheSurroundingColor()
    {
        var original = Room(withClutter: true);
        var tidy = Room(withClutter: false);
        // The redraw's whole floor drifted darker, and the box is drawn tight around the shoe.
        var edited = Resize(Darken(tidy, new PhotoRegion(0, 260, Width, Height - 260), amount: 30), Width / 2, Height / 2);
        PhotoEditRegion[] regions = [new(.4967m, .7467m, .07m, .08m)];

        var merge = PhotoEditMerger.MergeMasked(original, edited, regions);

        // A few pixels inside the box edge the patch matches the floor beside it rather than stepping darker.
        foreach (var (x, y) in new[] { (302, 315), (337, 315), (320, 302), (320, 327) })
        {
            AssertClose(tidy, merge.Photo, x, y, tolerance: 14);
        }
    }

    [Fact]
    public void PhotoEditMaskRoundTripsAndLimitsTheAllowedArea()
    {
        PhotoEditRegion[] regions = [new(.1m, .2m, .2m, .3m), new(.6m, .5m, .1m, .1m)];

        var decoded = PhotoEditMask.Decode(PhotoEditMask.Encode(regions));

        Assert.Equal(regions, decoded);
        Assert.Throws<ArgumentOutOfRangeException>(() => PhotoEditMask.Encode([new(0, 0, .8m, .8m)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhotoEditMask.Encode([new(.9m, .9m, .2m, .2m)]));
    }

    // A synthetic room: a wall with doorways and a window, a floor with boards, and an optional shoe.
    private static PhotoPixels Room(bool withClutter, int seed = 0, int offsetX = 0, int offsetY = 0)
    {
        var random = new Random(seed);
        var doors = seed == 0 ? new[] { 60, 380 } : new[] { random.Next(0, 250), random.Next(300, 520) };
        var horizon = seed == 0 ? 260 : random.Next(120, 200);
        var rgb = new byte[Width * Height * 3];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (sx, sy) = (x + offsetX, y + offsetY);
                (int R, int G, int B) color = sy < horizon
                    ? (210 - sy / 8, 200 - sy / 8, 180 - sy / 8)
                    : (140 + 30 * (sx / 50 % 2), 100 + 20 * (sx / 50 % 2), 60);
                if (sy < horizon && doors.Any(door => sx >= door && sx < door + 80 && sy > 70))
                {
                    color = (90, 70, 50);
                }

                if (sy is > 60 and < 160 && sx is > 200 and < 320)
                {
                    color = (150, 190, 230);
                }

                if (withClutter
                    && x >= Clutter.X && x < Clutter.X + Clutter.Width && y >= Clutter.Y && y < Clutter.Y + Clutter.Height)
                {
                    color = (20, 20, 25);
                }

                var offset = (y * Width + x) * 3;
                rgb[offset] = (byte)color.R;
                rgb[offset + 1] = (byte)color.G;
                rgb[offset + 2] = (byte)color.B;
            }
        }

        return new PhotoPixels(Width, Height, rgb);
    }

    private static PhotoPixels Resize(PhotoPixels photo, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = (y * photo.Height / height * photo.Width + x * photo.Width / width) * 3;
                photo.Rgb.AsSpan(source, 3).CopyTo(rgb.AsSpan((y * width + x) * 3, 3));
            }
        }

        return new PhotoPixels(width, height, rgb);
    }

    private static PhotoPixels Shift(PhotoPixels photo, int red, int green, int blue)
    {
        var rgb = new byte[photo.Rgb.Length];
        int[] shifts = [red, green, blue];
        for (var offset = 0; offset < rgb.Length; offset++)
        {
            rgb[offset] = (byte)Math.Clamp(photo.Rgb[offset] + shifts[offset % 3], 0, 255);
        }

        return new PhotoPixels(photo.Width, photo.Height, rgb);
    }

    private static PhotoPixels Darken(PhotoPixels photo, PhotoRegion area, int amount)
    {
        var rgb = (byte[])photo.Rgb.Clone();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var offset = (y * photo.Width + x) * 3 + channel;
                    rgb[offset] = (byte)Math.Max(0, rgb[offset] - amount);
                }
            }
        }

        return new PhotoPixels(photo.Width, photo.Height, rgb);
    }

    private static (byte, byte, byte) Pixel(PhotoPixels photo, int x, int y)
    {
        var offset = (y * photo.Width + x) * 3;
        return (photo.Rgb[offset], photo.Rgb[offset + 1], photo.Rgb[offset + 2]);
    }

    private static void AssertClose(PhotoPixels expected, PhotoPixels actual, int x, int y, int tolerance)
    {
        var (er, eg, eb) = Pixel(expected, x, y);
        var (ar, ag, ab) = Pixel(actual, x, y);
        Assert.True(Math.Abs(er - ar) <= tolerance && Math.Abs(eg - ag) <= tolerance && Math.Abs(eb - ab) <= tolerance,
            $"At ({x}, {y}) expected {(er, eg, eb)} but got {(ar, ag, ab)}.");
        Assert.InRange(Math.Abs(er - ar), 0, tolerance);
        Assert.InRange(Math.Abs(eg - ag), 0, tolerance);
        Assert.InRange(Math.Abs(eb - ab), 0, tolerance);
    }
}
