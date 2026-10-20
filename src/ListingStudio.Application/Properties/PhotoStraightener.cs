namespace ListingStudio.Application.Properties;

/// <summary>The locally measured tilt and the level, edge-safe crop made from it.</summary>
public sealed record PhotoStraighteningResult(PhotoPixels Photo, double TiltDegrees);

/// <summary>
/// Levels small camera-roll errors from strong horizontal and vertical edges. It does not invent pixels or correct
/// perspective: the result is cropped inside the rotated photo so no empty corners remain.
/// </summary>
public static class PhotoStraightener
{
    public const double MaximumTiltDegrees = 8;
    public const double MinimumUsefulTiltDegrees = 0.35;

    private const int MaximumAnalysisDimension = 640;
    private const int MinimumGradient = 24;
    private const double AngleStepDegrees = 0.25;

    public static PhotoStraighteningResult? TryStraightenAndCrop(
        PhotoPixels source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tilt = MeasureTilt(source, cancellationToken);
        if (tilt is null || Math.Abs(tilt.Value) < MinimumUsefulTiltDegrees)
        {
            return null;
        }

        return new PhotoStraighteningResult(RotateIntoSafeCrop(source, -tilt.Value, cancellationToken), tilt.Value);
    }

    private static double? MeasureTilt(PhotoPixels source, CancellationToken cancellationToken)
    {
        var scale = Math.Min(1, MaximumAnalysisDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(3, (int)Math.Round(source.Width * scale));
        var height = Math.Max(3, (int)Math.Round(source.Height * scale));
        var gray = DownsampleGray(source, width, height);
        var minimumGradientSquared = MinimumGradient * MinimumGradient;
        var edges = new List<EdgePoint>(width * height / 8);

        for (var y = 1; y < height - 1; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 1; x < width - 1; x++)
            {
                var top = (y - 1) * width + x;
                var middle = y * width + x;
                var bottom = (y + 1) * width + x;
                var gx = gray[top + 1] + 2 * gray[middle + 1] + gray[bottom + 1]
                    - gray[top - 1] - 2 * gray[middle - 1] - gray[bottom - 1];
                var gy = gray[bottom - 1] + 2 * gray[bottom] + gray[bottom + 1]
                    - gray[top - 1] - 2 * gray[top] - gray[top + 1];
                var magnitudeSquared = gx * gx + gy * gy;
                if (magnitudeSquared < minimumGradientSquared)
                {
                    continue;
                }

                edges.Add(new EdgePoint(x, y, Math.Sqrt(magnitudeSquared)));
            }
        }

        var minimumSupport = Math.Max(48, width * height / 2_000);
        if (edges.Count < minimumSupport)
        {
            return null;
        }

        // A small Hough-style search works on complete lines rather than individual stair-stepped raster edges. For
        // each candidate roll, structural edges vote into horizontal and vertical projection bins. Correctly aligned
        // lines concentrate those votes and produce the largest squared-bin score.
        var diagonal = (int)Math.Ceiling(Math.Sqrt(width * width + height * height));
        var horizontal = new double[diagonal + 3];
        var vertical = new double[diagonal + 3];
        var offset = (diagonal + 2) / 2.0;
        var bestAngle = 0.0;
        var bestScore = double.MinValue;
        var zeroScore = 0.0;
        var steps = (int)Math.Round(MaximumTiltDegrees / AngleStepDegrees);
        for (var step = -steps; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = step * AngleStepDegrees;
            var radians = angle * Math.PI / 180;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            Array.Clear(horizontal);
            Array.Clear(vertical);
            foreach (var edge in edges)
            {
                var x = edge.X - (width - 1) / 2.0;
                var y = edge.Y - (height - 1) / 2.0;
                var horizontalBin = Math.Clamp((int)Math.Round(-x * sine + y * cosine + offset), 0, horizontal.Length - 1);
                var verticalBin = Math.Clamp((int)Math.Round(x * cosine + y * sine + offset), 0, vertical.Length - 1);
                horizontal[horizontalBin] += edge.Weight;
                vertical[verticalBin] += edge.Weight;
            }

            var score = SquaredSum(horizontal) + SquaredSum(vertical);
            if (step == 0)
            {
                zeroScore = score;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return Math.Abs(bestAngle) >= MinimumUsefulTiltDegrees && bestScore > zeroScore * 1.01
            ? bestAngle
            : null;
    }

    private static double SquaredSum(double[] bins)
    {
        double score = 0;
        foreach (var value in bins)
        {
            score += value * value;
        }

        return score;
    }

    private static byte[] DownsampleGray(PhotoPixels source, int width, int height)
    {
        var gray = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(source.Height - 1, (int)((y + 0.5) * source.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(source.Width - 1, (int)((x + 0.5) * source.Width / width));
                var index = (sourceY * source.Width + sourceX) * 3;
                gray[y * width + x] = (byte)((54 * source.Rgb[index]
                    + 183 * source.Rgb[index + 1]
                    + 19 * source.Rgb[index + 2]) >> 8);
            }
        }

        return gray;
    }

    private static PhotoPixels RotateIntoSafeCrop(
        PhotoPixels source,
        double correctionDegrees,
        CancellationToken cancellationToken)
    {
        var radians = correctionDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var absoluteSine = Math.Abs(sine);
        var (width, height) = SafeCropSize(source.Width, source.Height, cosine, absoluteSine);

        var pixels = new byte[width * height * 3];
        var sourceCenterX = (source.Width - 1) / 2.0;
        var sourceCenterY = (source.Height - 1) / 2.0;
        var destinationCenterX = (width - 1) / 2.0;
        var destinationCenterY = (height - 1) / 2.0;
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offsetY = y - destinationCenterY;
            for (var x = 0; x < width; x++)
            {
                var offsetX = x - destinationCenterX;
                var sourceX = sourceCenterX + offsetX * cosine + offsetY * sine;
                var sourceY = sourceCenterY - offsetX * sine + offsetY * cosine;
                SampleBilinear(source, sourceX, sourceY, pixels, (y * width + x) * 3);
            }
        }

        return new PhotoPixels(width, height, pixels);
    }

    private static void SampleBilinear(PhotoPixels source, double x, double y, byte[] destination, int destinationIndex)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        var left = Math.Clamp((int)Math.Floor(x), 0, source.Width - 1);
        var top = Math.Clamp((int)Math.Floor(y), 0, source.Height - 1);
        var right = Math.Min(left + 1, source.Width - 1);
        var bottom = Math.Min(top + 1, source.Height - 1);
        var xWeight = x - Math.Floor(x);
        var yWeight = y - Math.Floor(y);
        for (var channel = 0; channel < 3; channel++)
        {
            var topLeft = source.Rgb[(top * source.Width + left) * 3 + channel];
            var topRight = source.Rgb[(top * source.Width + right) * 3 + channel];
            var bottomLeft = source.Rgb[(bottom * source.Width + left) * 3 + channel];
            var bottomRight = source.Rgb[(bottom * source.Width + right) * 3 + channel];
            var topValue = topLeft + (topRight - topLeft) * xWeight;
            var bottomValue = bottomLeft + (bottomRight - bottomLeft) * xWeight;
            destination[destinationIndex + channel] = (byte)Math.Clamp(
                Math.Round(topValue + (bottomValue - topValue) * yWeight),
                byte.MinValue,
                byte.MaxValue);
        }
    }

    private static (int Width, int Height) SafeCropSize(int width, int height, double cosine, double sine)
    {
        double cropWidth;
        double cropHeight;
        var widthIsLonger = width >= height;
        var longSide = widthIsLonger ? width : height;
        var shortSide = widthIsLonger ? height : width;
        if (shortSide <= 2 * sine * cosine * longSide || Math.Abs(sine - cosine) < 1e-10)
        {
            var halfShortSide = 0.5 * shortSide;
            if (widthIsLonger)
            {
                cropWidth = halfShortSide / sine;
                cropHeight = halfShortSide / cosine;
            }
            else
            {
                cropWidth = halfShortSide / cosine;
                cropHeight = halfShortSide / sine;
            }
        }
        else
        {
            var denominator = cosine * cosine - sine * sine;
            cropWidth = (width * cosine - height * sine) / denominator;
            cropHeight = (height * cosine - width * sine) / denominator;
        }

        return (
            Math.Clamp((int)Math.Floor(cropWidth), 1, width),
            Math.Clamp((int)Math.Floor(cropHeight), 1, height));
    }

    private readonly record struct EdgePoint(int X, int Y, double Weight);
}
