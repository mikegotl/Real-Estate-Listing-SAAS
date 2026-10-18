namespace ListingStudio.Application.Properties;

/// <summary>A decoded photo: 8-bit RGB pixels, row by row, three bytes per pixel.</summary>
public sealed record PhotoPixels
{
    public PhotoPixels(int width, int height, byte[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.LongLength != (long)width * height * 3)
        {
            throw new ArgumentException("The pixel buffer does not match the photo's dimensions.", nameof(rgb));
        }

        Width = width;
        Height = height;
        Rgb = rgb;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgb { get; }
}

/// <summary>A rectangle inside a photo, in pixels.</summary>
public readonly record struct PhotoRegion(int X, int Y, int Width, int Height);

public enum PhotoScaling
{
    /// <summary>Keeps the photo's size unless it is larger than the box, then shrinks it to fit.</summary>
    ShrinkToFit,

    /// <summary>Scales the photo up or down to the largest size that fits the box, keeping its proportions.</summary>
    Fit,

    /// <summary>Scales the photo to exactly the box.</summary>
    Exact,
}

/// <summary>Decodes and encodes photos, applying any camera orientation so pixels are upright.</summary>
public interface IPhotoImageCodec
{
    Task<PhotoPixels> DecodeAsync(
        byte[] image,
        string mimeType,
        int boxWidth,
        int boxHeight,
        PhotoScaling scaling,
        CancellationToken cancellationToken = default);

    Task<byte[]> EncodeJpegAsync(PhotoPixels photo, CancellationToken cancellationToken = default);

    Task<byte[]> EncodePngAsync(PhotoPixels photo, CancellationToken cancellationToken = default);

    /// <summary>A PNG edit mask: transparent where <paramref name="editable"/> is true, opaque elsewhere.</summary>
    Task<byte[]> EncodeMaskPngAsync(
        int width,
        int height,
        PhotoRegion editable,
        CancellationToken cancellationToken = default);
}
