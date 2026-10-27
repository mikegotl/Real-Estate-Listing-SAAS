using ListingStudio.Application.Properties;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>Builds a social-ready crop without manufacturing or altering any property features.</summary>
public static class CampaignStillPostComposer
{
    private const byte AccentRed = 205;
    private const byte AccentGreen = 158;
    private const byte AccentBlue = 76;

    public static PhotoPixels Compose(PhotoPixels source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var output = new byte[checked(width * height * 3)];
        var scale = Math.Max(width / (double)source.Width, height / (double)source.Height);
        var visibleWidth = width / scale;
        var visibleHeight = height / scale;
        var sourceLeft = (source.Width - visibleWidth) / 2d;
        var sourceTop = (source.Height - visibleHeight) / 2d;

        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)(sourceTop + (y + 0.5d) / scale), 0, source.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)(sourceLeft + (x + 0.5d) / scale), 0, source.Width - 1);
                var sourceOffset = (sourceY * source.Width + sourceX) * 3;
                var targetOffset = (y * width + x) * 3;
                output[targetOffset] = source.Rgb[sourceOffset];
                output[targetOffset + 1] = source.Rgb[sourceOffset + 1];
                output[targetOffset + 2] = source.Rgb[sourceOffset + 2];
            }
        }

        // A subtle bottom fade keeps the image usable with Facebook's caption and interface overlays.
        var fadeStart = height * 3 / 4;
        for (var y = fadeStart; y < height; y++)
        {
            var amount = 0.42d * (y - fadeStart) / Math.Max(1, height - fadeStart - 1);
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                output[offset] = (byte)(output[offset] * (1d - amount));
                output[offset + 1] = (byte)(output[offset + 1] * (1d - amount));
                output[offset + 2] = (byte)(output[offset + 2] * (1d - amount));
            }
        }

        var accentHeight = Math.Max(4, height / 180);
        for (var y = height - accentHeight; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                output[offset] = AccentRed;
                output[offset + 1] = AccentGreen;
                output[offset + 2] = AccentBlue;
            }
        }

        return new PhotoPixels(width, height, output);
    }
}
