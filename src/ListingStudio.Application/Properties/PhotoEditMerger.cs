using ListingStudio.Domain.Properties;

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

    // Inside an agent's box, a smaller difference counts, so faint shadows go with the item, and the pasted area
    // grows a little further to take the item's soft edges too.
    private const int MaskedChangedThreshold = 16;
    private const int MaskedDilateCells = 3;

    // A box's edge fades over this share of its shorter side, between a few pixels and a sliver of the photo. The
    // color is already matched at the seam, so the fade stays narrow and a box drawn tight still removes the item.
    private const double BoxFadeShare = 0.06;
    private const double MinimumBoxFadePixels = 2;
    private const double MaximumBoxFadeShare = 0.008;

    private const int SeamIterations = 400;
    private const double SeamOverRelaxation = 1.9;

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

    /// <summary>
    /// Keeps every pixel outside the agent-approved regions exactly as uploaded and takes generated pixels only from
    /// inside those regions. The agent reviews the result before it is allowed to replace the listing photo.
    /// </summary>
    /// <remarks>
    /// Pasting a whole box leaves a visible rectangle: the edit is a quarter of the photo's resolution, its local
    /// color drifts from the original's, and the model nudges lines it did not mean to change. So inside a box only
    /// the areas the edit actually changed are taken, the edit is resampled smoothly, its color is bent to meet the
    /// original's at every seam, and the box's own edge fades over a width that grows with the box.
    /// </remarks>
    public static PhotoEditMerge MergeMasked(
        PhotoPixels original,
        PhotoPixels edited,
        IReadOnlyCollection<PhotoEditRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        PhotoEditMask.Encode(regions);

        var gridWidth = Math.Min(GridWidth, original.Width);
        var gridHeight = Math.Max(1, (int)Math.Round(original.Height * gridWidth / (double)original.Width));
        var before = Downscale(original, gridWidth, gridHeight);
        var after = Downscale(edited, gridWidth, gridHeight);
        var tone = ToneMatch.Between(after, before);
        tone.Apply(after);

        // First measure how the edit's color drifts from the original's across the frame, leaving out only the
        // areas it clearly redrew; then a cell counts as changed when it differs from that local drift.
        var cells = gridWidth * gridHeight;
        var redrawn = new bool[cells];
        for (var cell = 0; cell < cells; cell++)
        {
            redrawn[cell] = Difference(before, after, cell) > ChangedThreshold;
        }

        var drift = DriftField(before, after, redrawn, gridWidth, gridHeight);
        var changed = new bool[cells];
        var changedInside = 0;
        var selectedCells = 0;
        for (var y = 0; y < gridHeight; y++)
        {
            for (var x = 0; x < gridWidth; x++)
            {
                var nx = (x + .5) / gridWidth;
                var ny = (y + .5) / gridHeight;
                if (!regions.Any(region => Contains(region, nx, ny)))
                {
                    continue;
                }

                var cell = y * gridWidth + x;
                selectedCells++;
                if (Difference(before, after, cell) > ChangedThreshold / 2)
                {
                    changedInside++;
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    var offset = cell * 3 + channel;
                    changed[cell] |= Math.Abs(before[offset] - after[offset] - drift[offset]) > MaskedChangedThreshold;
                }
            }
        }

        if (selectedCells == 0 || changedInside == 0)
        {
            throw new PhotoFixRejectedException("The AI found no removable clutter in the selected areas, so the photo was left as it was.");
        }

        var pasted = Dilate(changed, gridWidth, gridHeight, MaskedDilateCells);
        var alpha = Feather(pasted, gridWidth, gridHeight);
        // Cells the edit redrew outside a box, such as the rest of an item that spills past it, say nothing about
        // color drift, so they are filled in like the pasted ones.
        var unknown = new bool[cells];
        for (var cell = 0; cell < cells; cell++)
        {
            unknown[cell] = pasted[cell] || redrawn[cell];
        }

        var correction = DriftField(before, after, unknown, gridWidth, gridHeight);
        var boxes = regions.Select(region => PixelBox.For(region, original.Width, original.Height)).ToArray();

        var result = (byte[])original.Rgb.Clone();
        var rowWeights = new double[original.Width];
        for (var y = 0; y < original.Height; y++)
        {
            var centerY = y + .5;
            int first = original.Width, last = -1;
            foreach (var box in boxes)
            {
                if (centerY < box.Top || centerY > box.Bottom)
                {
                    continue;
                }

                for (var x = Math.Max(0, (int)Math.Floor(box.Left)); x < Math.Min(original.Width, (int)Math.Ceiling(box.Right)); x++)
                {
                    var weight = box.Weight(x + .5, centerY);
                    if (weight > rowWeights[x])
                    {
                        rowWeights[x] = weight;
                        first = Math.Min(first, x);
                        last = Math.Max(last, x);
                    }
                }
            }

            var gridY = centerY * gridHeight / original.Height - .5;
            var editedY = centerY * edited.Height / original.Height - .5;
            for (var x = first; x <= last; x++)
            {
                var boxWeight = rowWeights[x];
                rowWeights[x] = 0;
                if (boxWeight <= 0)
                {
                    continue;
                }

                var gridX = (x + .5) * gridWidth / original.Width - .5;
                var weight = boxWeight * Bilinear(alpha, gridWidth, gridHeight, gridX, gridY);
                if (weight <= 0)
                {
                    continue;
                }

                var editedX = (x + .5) * edited.Width / original.Width - .5;
                var target = (y * original.Width + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var patch = tone.Map(channel, Sample(edited, editedX, editedY, channel))
                        + BilinearChannel(correction, gridWidth, gridHeight, gridX, gridY, channel);
                    result[target + channel] = (byte)Math.Clamp(
                        Math.Round(result[target + channel] * (1 - weight) + patch * weight), 0, 255);
                }
            }
        }

        return new PhotoEditMerge(
            new PhotoPixels(original.Width, original.Height, result),
            changedInside / (double)Math.Max(1, cells));
    }

    private static bool Contains(PhotoEditRegion region, double x, double y) =>
        x >= (double)region.X && x <= (double)(region.X + region.Width)
        && y >= (double)region.Y && y <= (double)(region.Y + region.Height);

    private static int Difference(byte[] before, byte[] after, int cell)
    {
        var offset = cell * 3;
        return Math.Max(
            Math.Abs(before[offset] - after[offset]),
            Math.Max(Math.Abs(before[offset + 1] - after[offset + 1]), Math.Abs(before[offset + 2] - after[offset + 2])));
    }

    // The edit's color rarely matches the original's locally even after the overall tone match, which shows as a
    // step at the edge of a pasted patch. Where the edit left the photo alone, the difference between the two is
    // exactly that drift; it is measured there and spread smoothly across the unknown cells (a membrane fill), so
    // a patch corrected by it meets the original's color at every seam while keeping the edit's own shading inside.
    private static double[] DriftField(byte[] before, byte[] after, bool[] unknown, int width, int height)
    {
        var cells = width * height;
        var raw = new double[cells * 3];
        for (var cell = 0; cell < cells; cell++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                raw[cell * 3 + channel] = before[cell * 3 + channel] - after[cell * 3 + channel];
            }
        }

        // Average the measured drift over its known neighbors, so texture noise in a single cell does not tint a patch.
        var field = new double[cells * 3];
        var fill = new List<int>();
        Span<double> sums = stackalloc double[3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                if (unknown[cell])
                {
                    fill.Add(cell);
                    continue;
                }

                var count = 0;
                sums.Clear();
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height || unknown[ny * width + nx])
                        {
                            continue;
                        }

                        count++;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            sums[channel] += raw[(ny * width + nx) * 3 + channel];
                        }
                    }
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    field[cell * 3 + channel] = sums[channel] / count;
                }
            }
        }

        if (fill.Count == 0 || fill.Count == cells)
        {
            return field;
        }

        // Over-relaxed Gauss-Seidel for Laplace's equation, with the known drift as the boundary.
        for (var iteration = 0; iteration < SeamIterations; iteration++)
        {
            foreach (var cell in fill)
            {
                var (x, y) = (cell % width, cell / width);
                var neighbors = 0;
                sums.Clear();
                foreach (var (nx, ny) in (ReadOnlySpan<(int, int)>)[(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)])
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    neighbors++;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        sums[channel] += field[(ny * width + nx) * 3 + channel];
                    }
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    var current = field[cell * 3 + channel];
                    field[cell * 3 + channel] = current + SeamOverRelaxation * (sums[channel] / neighbors - current);
                }
            }
        }

        return field;
    }

    // Bilinear sampling, so an edit smaller than the photo is enlarged smoothly instead of in visible blocks.
    private static double Sample(PhotoPixels photo, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, photo.Width - 1);
        y = Math.Clamp(y, 0, photo.Height - 1);
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(photo.Width - 1, x0 + 1), Math.Min(photo.Height - 1, y0 + 1));
        var (fx, fy) = (x - x0, y - y0);
        double At(int px, int py) => photo.Rgb[(py * photo.Width + px) * 3 + channel];
        var top = At(x0, y0) * (1 - fx) + At(x1, y0) * fx;
        var bottom = At(x0, y1) * (1 - fx) + At(x1, y1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static double BilinearChannel(double[] grid, int width, int height, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(width - 1, x0 + 1), Math.Min(height - 1, y0 + 1));
        var (fx, fy) = (x - x0, y - y0);
        var top = grid[(y0 * width + x0) * 3 + channel] * (1 - fx) + grid[(y0 * width + x1) * 3 + channel] * fx;
        var bottom = grid[(y1 * width + x0) * 3 + channel] * (1 - fx) + grid[(y1 * width + x1) * 3 + channel] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    // An agent-drawn box in pixels. Its weight is 1 well inside and fades to 0 at its edge, over a width that grows
    // with the box so a large box gets a wide, soft seam and a small one still keeps most of its area.
    private readonly record struct PixelBox(double Left, double Top, double Right, double Bottom, double Fade)
    {
        public static PixelBox For(PhotoEditRegion region, int width, int height)
        {
            var (left, top) = ((double)region.X * width, (double)region.Y * height);
            var (right, bottom) = (left + (double)region.Width * width, top + (double)region.Height * height);
            var fade = Math.Clamp(
                Math.Min(right - left, bottom - top) * BoxFadeShare,
                MinimumBoxFadePixels,
                Math.Max(MinimumBoxFadePixels, Math.Max(width, height) * MaximumBoxFadeShare));
            return new PixelBox(left, top, right, bottom, fade);
        }

        public double Weight(double x, double y)
        {
            if (x < Left || x > Right || y < Top || y > Bottom)
            {
                return 0;
            }

            var distance = Math.Min(Math.Min(x - Left, Right - x), Math.Min(y - Top, Bottom - y));
            var t = Math.Clamp(distance / Fade, 0, 1);
            return t * t * (3 - 2 * t);
        }
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

    private static bool[] Dilate(bool[] cells, int width, int height, int radius = DilateCells)
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

                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
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

        public double Map(int channel, double value) => value * scale[channel] + shift[channel];

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
