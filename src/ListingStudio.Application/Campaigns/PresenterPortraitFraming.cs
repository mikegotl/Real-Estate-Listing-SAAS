using ListingStudio.Application.Properties;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Frames photos for a vertical Kling 4 presenter video. Kling returns a video in the shape of its image_url photo
/// (the first tour photo) and ignores the requested aspect ratio, so a landscape room photo gives a landscape video
/// that the renderer then has to crop. Cropping that photo (and the headshot reference) to 9:16 first makes Kling
/// return a true vertical video.
/// </summary>
public static class PresenterPortraitFraming
{
    public const int VerticalWidthRatio = 9;
    public const int VerticalHeightRatio = 16;

    /// <summary>
    /// Crops the photo to 9:16. A wider photo keeps its full height and loses equal parts of both sides; a taller one
    /// keeps its full width and is cut mostly from the bottom, because faces sit in the upper part of a headshot.
    /// A photo that is already within one pixel of 9:16 is returned unchanged.
    /// </summary>
    public static PhotoPixels CropToVertical(PhotoPixels source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var width = source.Width;
        var height = source.Height;
        var left = 0;
        var top = 0;
        if ((long)source.Width * VerticalHeightRatio > (long)source.Height * VerticalWidthRatio)
        {
            width = Math.Max(1, (int)((long)source.Height * VerticalWidthRatio / VerticalHeightRatio));
            left = (source.Width - width) / 2;
        }
        else
        {
            height = Math.Max(1, (int)((long)source.Width * VerticalHeightRatio / VerticalWidthRatio));
            top = (source.Height - height) / 4;
        }

        if (source.Width - width <= 1 && source.Height - height <= 1)
        {
            return source;
        }

        var output = new byte[checked(width * height * 3)];
        var sourceStride = source.Width * 3;
        var outputStride = width * 3;
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(source.Rgb, (top + row) * sourceStride + left * 3, output, row * outputStride, outputStride);
        }

        return new PhotoPixels(width, height, output);
    }
}
