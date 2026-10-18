namespace ListingStudio.Application.Properties;

/// <summary>
/// Fits a photo onto one of the fixed canvases an AI image editor works on, and cuts the edited photo back out.
/// The photo is scaled to fit the canvas without cropping and the rest is padded, so the edit cannot reframe the
/// room, and cutting the same region back out keeps the photo's original proportions.
/// </summary>
public static class PhotoEditCanvas
{
    public const byte PaddingGray = 128;

    /// <summary>The editor canvas closest to the photo's shape: landscape, portrait or square.</summary>
    public static (int Width, int Height) SizeFor(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var aspect = width / (double)height;
        return aspect switch
        {
            >= 1.2 => (1_536, 1_024),
            <= 1 / 1.2 => (1_024, 1_536),
            _ => (1_024, 1_024),
        };
    }

    /// <summary>Centers a photo that already fits the canvas on a plain gray canvas.</summary>
    public static PhotoPixels Pad(PhotoPixels photo, int canvasWidth, int canvasHeight, out PhotoRegion region)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Width > canvasWidth || photo.Height > canvasHeight)
        {
            throw new ArgumentException("The photo must be scaled to fit the canvas first.", nameof(photo));
        }

        region = new PhotoRegion(
            (canvasWidth - photo.Width) / 2,
            (canvasHeight - photo.Height) / 2,
            photo.Width,
            photo.Height);
        var canvas = new byte[canvasWidth * canvasHeight * 3];
        canvas.AsSpan().Fill(PaddingGray);
        var rowBytes = photo.Width * 3;
        for (var row = 0; row < photo.Height; row++)
        {
            photo.Rgb.AsSpan(row * rowBytes, rowBytes)
                .CopyTo(canvas.AsSpan(((region.Y + row) * canvasWidth + region.X) * 3, rowBytes));
        }

        return new PhotoPixels(canvasWidth, canvasHeight, canvas);
    }

    public static PhotoPixels Crop(PhotoPixels canvas, PhotoRegion region)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X + region.Width > canvas.Width || region.Y + region.Height > canvas.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The region must lie inside the canvas.");
        }

        var rowBytes = region.Width * 3;
        var pixels = new byte[rowBytes * region.Height];
        for (var row = 0; row < region.Height; row++)
        {
            canvas.Rgb.AsSpan(((region.Y + row) * canvas.Width + region.X) * 3, rowBytes)
                .CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
        }

        return new PhotoPixels(region.Width, region.Height, pixels);
    }
}
