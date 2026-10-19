namespace ListingStudio.Application.Properties;

/// <summary>
/// Keeps an AI clutter edit honest. An image model redraws the whole frame even when asked to change only a few
/// items, so its output is never used as the photo. Instead the original photo is kept at full resolution and only
/// the small areas where the edit differs from it are pasted in. When the edit differs across a large share of the
/// frame, it has re-imagined the room rather than tidied it, and it is rejected so the listing never shows a room
/// that is not the property.
/// </summary>
public static class PhotoEditMerger
{
    // The photos are compared on a coarse grid, so noise, compression and a pixel or two of drift do not count.
    private const int GridWidth = 192;

    // A cell counts as changed when, after matching overall tone, a channel differs by more than this (0 to 255).
    private const int ChangedThreshold = 48;

    // Clutter covers a small part of a photo; an edit that changes more than this has redrawn the room.
    public const double MaximumChangedFraction = 0.2;

    // Across the cells the edit left alone, the two photos must line up this closely, or the room itself moved.
    private const double MinimumStructureCorrelation = 0.8;

    // Grow each changed area slightly so a removed item's edges and shadow go with it, then soften the seam.
    private const int DilateCells = 2;
    private const int FeatherCells = 2;

    /// <summary>
    /// Returns the original photo with the edit's changed areas pasted in. <paramref name="edited"/> is the edit cut
    /// back to the photo's proportions at any size. Throws <see cref="PhotoFixRejectedException"/> when the edit
    /// changed nothing or changed the room itself.
    /// </summary>
    public static PhotoEditMerge Merge(PhotoPixels original, PhotoPixels edited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        var gridWidth = Math.Min(GridWidth, original.Width);
        var gridHeight = Math.Max(1, (int)Math.Round(original.Height * gridWidth / (double)original.Width));
        var before = Downscale(original, gridWidth, gridHeight);
        var after = Downscale(edited, gridWidth, gridHeight);

        // The model also shifts the whole photo's color and exposure; match that first so only real changes count.
        var tone = ToneMatch.Between(after, before);
        tone.Apply(after);

        var cells = gridWidth * gridHeight;
        var changed = new bool[cells];
        var changedCount = 0;
        for (var cell = 0; cell < cells; cell++)
        {
            var offset = cell * 3;
            var difference = Math.Max(
                Math.Abs(before[offset] - after[offset]),
                Math.Max(Math.Abs(before[offset + 1] - after[offset + 1]), Math.Abs(before[offset + 2] - after[offset + 2])));
            if (difference > ChangedThreshold)
            {
                changed[cell] = true;
                changedCount++;
            }
        }

        var changedFraction = changedCount / (double)cells;
        var correlation = StructureCorrelation(before, after, changed, gridWidth, gridHeight);
        if (changedFraction > MaximumChangedFraction || correlation < MinimumStructureCorrelation)
        {
            throw new PhotoFixRejectedException(
                "The AI edit changed the room itself instead of only removing clutter, so the original photo was kept.");
        }

        if (changedCount == 0)
        {
            throw new PhotoFixRejectedException("The AI found no clutter to remove, so the photo was left as it was.");
        }

        var alpha = Feather(Dilate(changed, gridWidth, gridHeight), gridWidth, gridHeight);
        var result = (byte[])original.Rgb.Clone();
        for (var y = 0; y < original.Height; y++)
        {
            var gridY = (y + 0.5) * gridHeight / original.Height - 0.5;
            var editedY = Math.Min(edited.Height - 1, (int)((y + 0.5) * edited.Height / original.Height));
            for (var x = 0; x < original.Width; x++)
            {
                var weight = Bilinear(alpha, gridWidth, gridHeight, (x + 0.5) * gridWidth / original.Width - 0.5, gridY);
                if (weight <= 0)
                {
                    continue;
                }

                var editedX = Math.Min(edited.Width - 1, (int)((x + 0.5) * edited.Width / original.Width));
                var source = (editedY * edited.Width + editedX) * 3;
                var target = (y * original.Width + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var patch = tone.Map(channel, edited.Rgb[source + channel]);
                    result[target + channel] = (byte)Math.Round(
                        result[target + channel] * (1 - weight) + patch * weight);
                }
            }
        }

        return new PhotoEditMerge(new PhotoPixels(original.Width, original.Height, result), changedFraction);
    }

    // Box-averages a photo onto the comparison grid.
    private static byte[] Downscale(PhotoPixels photo, int width, int height)
    {
        var sums = new long[width * height * 3];
        var counts = new int[width * height];
        for (var y = 0; y < photo.Height; y++)
        {
            var gridRow = Math.Min(height - 1, y * height / photo.Height) * width;
            for (var x = 0; x < photo.Width; x++)
            {
                var cell = gridRow + Math.Min(width - 1, x * width / photo.Width);
                var source = (y * photo.Width + x) * 3;
                sums[cell * 3] += photo.Rgb[source];
                sums[cell * 3 + 1] += photo.Rgb[source + 1];
                sums[cell * 3 + 2] += photo.Rgb[source + 2];
                counts[cell]++;
            }
        }

        var grid = new byte[width * height * 3];
        for (var cell = 0; cell < counts.Length; cell++)
        {
            var count = Math.Max(1, counts[cell]);
            for (var channel = 0; channel < 3; channel++)
            {
                grid[cell * 3 + channel] = (byte)(sums[cell * 3 + channel] / count);
            }
        }

        return grid;
    }

    // Correlates the brightness edges of the two photos over the cells the edit left alone. A tidied photo keeps
    // every wall, doorway and window edge in place; a redrawn one does not.
    private static double StructureCorrelation(byte[] before, byte[] after, bool[] changed, int width, int height)
    {
        double sumBefore = 0, sumAfter = 0, sumProduct = 0, sumBeforeSquared = 0, sumAfterSquared = 0;
        long count = 0;
        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                var cell = y * width + x;
                if (changed[cell] || changed[cell + 1] || changed[cell + width])
                {
                    continue;
                }

                foreach (var neighbor in (ReadOnlySpan<int>)[cell + 1, cell + width])
                {
                    var edgeBefore = Luma(before, neighbor) - Luma(before, cell);
                    var edgeAfter = Luma(after, neighbor) - Luma(after, cell);
                    sumBefore += edgeBefore;
                    sumAfter += edgeAfter;
                    sumProduct += edgeBefore * edgeAfter;
                    sumBeforeSquared += edgeBefore * edgeBefore;
                    sumAfterSquared += edgeAfter * edgeAfter;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return 0;
        }

        var covariance = sumProduct / count - sumBefore / count * (sumAfter / count);
        var varianceBefore = sumBeforeSquared / count - Math.Pow(sumBefore / count, 2);
        var varianceAfter = sumAfterSquared / count - Math.Pow(sumAfter / count, 2);
        // A photo with no edges at all (a blank wall) has nothing to misplace.
        return varianceBefore < 1 && varianceAfter < 1
            ? 1
            : covariance / Math.Sqrt(Math.Max(varianceBefore, 1e-9) * Math.Max(varianceAfter, 1e-9));
    }

    private static bool[] Dilate(bool[] cells, int width, int height)
    {
        var grown = new bool[cells.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!cells[y * width + x])
                {
                    continue;
                }

                for (var dy = -DilateCells; dy <= DilateCells; dy++)
                {
                    for (var dx = -DilateCells; dx <= DilateCells; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height)
                        {
                            grown[ny * width + nx] = true;
                        }
                    }
                }
            }
        }

        return grown;
    }

    // A box blur of the changed areas, so pasted patches fade into the original instead of ending in a hard edge.
    // The core of every changed area stays fully replaced.
    private static double[] Feather(bool[] cells, int width, int height)
    {
        var alpha = new double[cells.Length];
        var size = (2 * FeatherCells + 1) * (2 * FeatherCells + 1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                if (cells[cell])
                {
                    alpha[cell] = 1;
                    continue;
                }

                var covered = 0;
                for (var dy = -FeatherCells; dy <= FeatherCells; dy++)
                {
                    for (var dx = -FeatherCells; dx <= FeatherCells; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height && cells[ny * width + nx])
                        {
                            covered++;
                        }
                    }
                }

                alpha[cell] = covered / (double)size;
            }
        }

        return alpha;
    }

    private static double Bilinear(double[] grid, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(width - 1, x0 + 1), Math.Min(height - 1, y0 + 1));
        var (fx, fy) = (x - x0, y - y0);
        var top = grid[y0 * width + x0] * (1 - fx) + grid[y0 * width + x1] * fx;
        var bottom = grid[y1 * width + x0] * (1 - fx) + grid[y1 * width + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static double Luma(byte[] grid, int cell) =>
        0.2126 * grid[cell * 3] + 0.7152 * grid[cell * 3 + 1] + 0.0722 * grid[cell * 3 + 2];

    // A per-channel linear map that gives the edit the original's average and contrast.
    private sealed class ToneMatch
    {
        private readonly double[] scale = new double[3];
        private readonly double[] shift = new double[3];

        public static ToneMatch Between(byte[] source, byte[] reference)
        {
            var match = new ToneMatch();
            for (var channel = 0; channel < 3; channel++)
            {
                var (sourceMean, sourceDeviation) = Statistics(source, channel);
                var (referenceMean, referenceDeviation) = Statistics(reference, channel);
                var scale = sourceDeviation < 1 ? 1 : Math.Clamp(referenceDeviation / sourceDeviation, 0.5, 2);
                match.scale[channel] = scale;
                match.shift[channel] = referenceMean - sourceMean * scale;
            }

            return match;
        }

        public byte Map(int channel, byte value) =>
            (byte)Math.Clamp(Math.Round(value * scale[channel] + shift[channel]), 0, 255);

        public void Apply(byte[] grid)
        {
            for (var offset = 0; offset < grid.Length; offset++)
            {
                grid[offset] = Map(offset % 3, grid[offset]);
            }
        }

        private static (double Mean, double Deviation) Statistics(byte[] grid, int channel)
        {
            double sum = 0, squares = 0;
            var count = grid.Length / 3;
            for (var offset = channel; offset < grid.Length; offset += 3)
            {
                sum += grid[offset];
                squares += grid[offset] * grid[offset];
            }

            var mean = sum / count;
            return (mean, Math.Sqrt(Math.Max(0, squares / count - mean * mean)));
        }
    }
}

/// <summary>The merged photo, and the share of the frame the AI edit changed (0 to 1).</summary>
public sealed record PhotoEditMerge(PhotoPixels Photo, double ChangedFraction);
