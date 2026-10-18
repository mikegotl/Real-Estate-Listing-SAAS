namespace ListingStudio.Application.Properties;

/// <summary>
/// The Brightness and color fix. It corrects only global tone, the way a photographer's auto levels would: a gentle
/// gray-world white balance removes a color cast, the darkest and brightest tones are stretched toward black and
/// white, and a gamma curve lifts dark photos toward an even mid-tone. Every pixel goes through the same per-channel
/// curve, so nothing in the photo is added, removed or moved.
/// </summary>
public static class PhotoToneNormalizer
{
    // The band a well-exposed listing photo's mid-tone lands in (0 to 1 after levels).
    private const double MinimumMedian = 0.5;
    private const double MaximumMedian = 0.65;

    // Percentiles of luminance taken as the photo's black and white points.
    private const double BlackPercentile = 0.005;
    private const double WhitePercentile = 0.995;

    // Limits keep the fix gentle: a dim room is lifted, never blown out, and lighting is evened, not replaced.
    private const int MaximumBlackPoint = 24;
    private const int MinimumWhitePoint = 200;
    private const double MinimumGamma = 0.6;
    private const double MaximumGamma = 1.15;
    private const double GammaDeadband = 0.04;
    private const double MinimumGain = 0.8;
    private const double MaximumGain = 1.25;
    private const double WhiteBalanceStrength = 0.75;

    // Measuring every pixel of a large photo adds time without changing the curve.
    private const int MaximumMeasuredPixels = 400_000;

    /// <summary>Measures the photo and applies the fix in place. Returns the curve applied.</summary>
    public static PhotoToneCurve Normalize(PhotoPixels photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var curve = Measure(photo.Rgb);
        Apply(photo.Rgb, curve);
        return curve;
    }

    public static PhotoToneCurve Measure(ReadOnlySpan<byte> rgb)
    {
        var pixelCount = rgb.Length / 3;
        if (pixelCount == 0)
        {
            throw new ArgumentException("A photo needs at least one pixel.", nameof(rgb));
        }

        var step = Math.Max(1, pixelCount / MaximumMeasuredPixels);
        var histogram = new long[256];
        long sampled = 0;
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 3;
            histogram[Luma(rgb[offset], rgb[offset + 1], rgb[offset + 2])]++;
            sampled++;
        }

        var midLow = Percentile(histogram, sampled, 0.05);
        var midHigh = Percentile(histogram, sampled, 0.97);

        // Gray world over the mid-tones: clipped highlights and deep shadows carry no reliable color.
        double red = 0, green = 0, blue = 0;
        long counted = 0;
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 3;
            var (r, g, b) = (rgb[offset], rgb[offset + 1], rgb[offset + 2]);
            var luma = Luma(r, g, b);
            if (luma < midLow || luma > midHigh || Math.Max(r, Math.Max(g, b)) >= 250)
            {
                continue;
            }

            red += r;
            green += g;
            blue += b;
            counted++;
        }

        var (redGain, greenGain, blueGain) = (1d, 1d, 1d);
        if (counted > 0 && red > 0 && green > 0 && blue > 0)
        {
            var gray = (red + green + blue) / 3;
            redGain = Gain(gray / red);
            greenGain = Gain(gray / green);
            blueGain = Gain(gray / blue);
        }

        // Levels and exposure are measured on the white-balanced photo, since balancing moves its tones.
        Array.Clear(histogram);
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 3;
            histogram[Luma(
                Scale(rgb[offset], redGain),
                Scale(rgb[offset + 1], greenGain),
                Scale(rgb[offset + 2], blueGain))]++;
        }

        var low = Percentile(histogram, sampled, BlackPercentile);
        var high = Percentile(histogram, sampled, WhitePercentile);
        var median = Percentile(histogram, sampled, 0.5);

        var blackPoint = Math.Min(low, MaximumBlackPoint);
        var whitePoint = Math.Max(high, MinimumWhitePoint);
        if (whitePoint - blackPoint < 64)
        {
            // A nearly flat photo (a wall, fog, a blank frame) has no range worth stretching.
            (blackPoint, whitePoint) = (0, 255);
        }

        // Listing photos read best on the bright side, so a mid-tone anywhere in the band is left alone: dim photos
        // are lifted to its bottom, and only a washed-out photo is brought down to its top.
        var stretchedMedian = Math.Clamp((median - blackPoint) / (double)(whitePoint - blackPoint), 0.01, 0.99);
        var gamma = stretchedMedian switch
        {
            < MinimumMedian => Math.Log(MinimumMedian) / Math.Log(stretchedMedian),
            > MaximumMedian => Math.Log(MaximumMedian) / Math.Log(stretchedMedian),
            _ => 1,
        };
        gamma = Math.Clamp(gamma, MinimumGamma, MaximumGamma);
        if (Math.Abs(gamma - 1) < GammaDeadband)
        {
            gamma = 1;
        }

        return new PhotoToneCurve(
            BuildTable(redGain, blackPoint, whitePoint, gamma),
            BuildTable(greenGain, blackPoint, whitePoint, gamma),
            BuildTable(blueGain, blackPoint, whitePoint, gamma),
            blackPoint,
            whitePoint,
            gamma);
    }

    public static void Apply(Span<byte> rgb, PhotoToneCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var (red, green, blue) = (curve.Red, curve.Green, curve.Blue);
        for (var offset = 0; offset + 2 < rgb.Length; offset += 3)
        {
            rgb[offset] = red[rgb[offset]];
            rgb[offset + 1] = green[rgb[offset + 1]];
            rgb[offset + 2] = blue[rgb[offset + 2]];
        }
    }

    private static byte Scale(byte value, double gain) => (byte)Math.Min(255, Math.Round(value * gain));

    private static double Gain(double fullCorrection) =>
        1 + (Math.Clamp(fullCorrection, MinimumGain, MaximumGain) - 1) * WhiteBalanceStrength;

    private static byte[] BuildTable(double gain, int blackPoint, int whitePoint, double gamma)
    {
        var table = new byte[256];
        var range = (double)(whitePoint - blackPoint);
        for (var value = 0; value < 256; value++)
        {
            var level = Math.Clamp((value * gain - blackPoint) / range, 0, 1);
            table[value] = (byte)Math.Round(Math.Pow(level, gamma) * 255);
        }

        return table;
    }

    private static int Percentile(long[] histogram, long total, double fraction)
    {
        var threshold = (long)Math.Ceiling(total * fraction);
        long running = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running >= Math.Max(1, threshold))
            {
                return value;
            }
        }

        return 255;
    }

    private static int Luma(byte red, byte green, byte blue) =>
        (int)Math.Round(0.2126 * red + 0.7152 * green + 0.0722 * blue);
}

/// <summary>The per-channel lookup tables a Brightness and color fix applied, and the levels behind them.</summary>
public sealed record PhotoToneCurve(
    byte[] Red,
    byte[] Green,
    byte[] Blue,
    int BlackPoint,
    int WhitePoint,
    double Gamma);
